#include "common.h"
#include "audio_math.h"
// 频谱参数由 src/services/analyserContract.ts 生成，两侧必须用同一份。
#include "spectrum_contract.h"
#include "bass.h"
#include "basswasapi.h"
#include "bassasio.h"
#include "bassmix.h"
#include <filesystem>
#include <vector>
#include <array>
#include <thread>
#include <atomic>
#include <mutex>
#include <chrono>
#include <algorithm>
#include <cmath>
#include <memory>
#include <stdexcept>

#define API extern "C" __declspec(dllexport)
namespace fs=std::filesystem;
static fs::path appDir;
static std::mutex gate;
static std::string errorText;
// 插件缺失不阻止初始化（常见格式仍能放），但不能静默：解码失败时把它带出来。
static std::string pluginWarning;
static bool initialized=false;
static thread_local std::string resultText;
static std::vector<HPLUGIN> plugins;

// 频谱参数（SPECTRUM_FFT / SPECTRUM_BINS / SPECTRUM_SMOOTHING / SPECTRUM_MIN_DB / SPECTRUM_MAX_DB）
// 来自 spectrum_contract.h，由 src/services/analyserContract.ts 生成。
// 两次计算的最小间隔。真正的节拍由窗口长度决定：2048 帧在 44.1k 下约 46ms（~22Hz），
// 48k 下约 42ms；这里只是下限，防止极低采样率时算得过密。
static constexpr uint64_t SPECTRUM_INTERVAL_MS=30;
// DSD 的可视化取样率。DSD 位流没法直接做 FFT：DoP 里每个 24-bit 样本装的是两个 DSD 字节，
// 原生模式干脆没有浮点样本。所以先做一阶 Sigma-Delta 解调（窗口内 1 的比例就是该时刻的电平），
// 抽到约 44.1k 再走同一套 FFT。定 44.1k 是为了和 PCM 的 bin 宽一致（44.1k/2048 约 21.5Hz）。
static constexpr double SPECTRUM_DSD_RATE=44100.0;
// 解调窗口的上下限，单位是 DSD 位。64 位约 1.45us：更短噪声太大，更长则低频被抹平。
static constexpr unsigned SPECTRUM_DSD_MIN_BITS=64;
static constexpr unsigned SPECTRUM_DSD_MAX_BITS=4096;

// 一个字节里 1 的个数。DSD 解调每个字节算一次；位运算版本只有几条指令，
// 不查表也不引平台 intrinsics，MSVC 与 MinGW 都能编。
static inline unsigned popcount8(uint8_t v){
    v=(uint8_t)(v-((v>>1)&0x55u));
    v=(uint8_t)((v&0x33u)+((v>>2)&0x33u));
    return (unsigned)((v+(v>>4))&0x0Fu);
}

// 一帧频谱走 base64：1024 个数用 JSON 传约 4KB，主进程还要解析成数字数组、过一遍结构化克隆；
// base64 只有 1.4KB，接收端一次解码就是字节。Base64 不含空格，所以代价只有一次字符串拼接。
static std::string base64Bytes(const uint8_t* data,size_t length){
    static const char alphabet[]="ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    std::string out;
    out.reserve((length+2)/3*4);
    size_t i=0;
    for(;i+3<=length;i+=3){
        uint32_t v=((uint32_t)data[i]<<16)|((uint32_t)data[i+1]<<8)|(uint32_t)data[i+2];
        out.push_back(alphabet[(v>>18)&63]);out.push_back(alphabet[(v>>12)&63]);
        out.push_back(alphabet[(v>>6)&63]);out.push_back(alphabet[v&63]);
    }
    if(i<length){
        uint32_t v=(uint32_t)data[i]<<16;
        if(i+1<length)v|=(uint32_t)data[i+1]<<8;
        out.push_back(alphabet[(v>>18)&63]);out.push_back(alphabet[(v>>12)&63]);
        out.push_back(i+1<length?alphabet[(v>>6)&63]:'=');out.push_back('=');
    }
    return out;
}

// 迭代式 radix-2 复数 FFT。只在这里用，规模固定 2048，不引第三方库。
static void spectrumFft(std::vector<float>& re,std::vector<float>& im){    const unsigned n=(unsigned)re.size();
    for(unsigned i=1,j=0;i<n;++i){
        unsigned bit=n>>1;
        for(;j&bit;bit>>=1)j^=bit;
        j^=bit;
        if(i<j){std::swap(re[i],re[j]);std::swap(im[i],im[j]);}
    }
    for(unsigned len=2;len<=n;len<<=1){
        double angle=-2.0*3.14159265358979323846/(double)len;
        double wr=std::cos(angle),wi=std::sin(angle);
        for(unsigned i=0;i<n;i+=len){
            double cr=1.0,ci=0.0;
            for(unsigned k=0;k<len/2;++k){
                unsigned a=i+k,b=a+len/2;
                float tr=(float)(re[b]*cr-im[b]*ci);
                float ti=(float)(re[b]*ci+im[b]*cr);
                re[b]=re[a]-tr;im[b]=im[a]-ti;
                re[a]+=tr;im[a]+=ti;
                double nextCr=cr*wr-ci*wi;ci=cr*wi+ci*wr;cr=nextCr;
            }
        }
    }
}

// BASS_WASAPI_INFO.format -> 设备实际位深；float 也按 32-bit 计。
static int wasapiFormatBits(DWORD format){
    switch(format){
        case BASS_WASAPI_FORMAT_8BIT:return 8;
        case BASS_WASAPI_FORMAT_16BIT:return 16;
        case BASS_WASAPI_FORMAT_24BIT:return 24;
        case BASS_WASAPI_FORMAT_32BIT:
        case BASS_WASAPI_FORMAT_FLOAT:return 32;
        default:return 0;
    }
}

// BASS_ASIO 通道格式 -> 位深；DSD 与未知格式返回 0。
static int asioFormatBits(DWORD format){
    if(format&BASS_ASIO_FORMAT_DITHER)format&=~BASS_ASIO_FORMAT_DITHER;
    switch(format){
        case BASS_ASIO_FORMAT_16BIT:return 16;
        case BASS_ASIO_FORMAT_24BIT:return 24;
        case BASS_ASIO_FORMAT_32BIT:
        case BASS_ASIO_FORMAT_32BIT16:
        case BASS_ASIO_FORMAT_32BIT18:
        case BASS_ASIO_FORMAT_32BIT20:
        case BASS_ASIO_FORMAT_32BIT24:
        case BASS_ASIO_FORMAT_FLOAT:return 32;
        default:return 0;
    }
}

class Worker {
    HANDLE process=nullptr, pipe=nullptr;
public:
    DsdHeader header{};
    ~Worker(){stop();}
    void cancel(){if(process)TerminateProcess(process,0);}
    void stop(){if(process){TerminateProcess(process,0);WaitForSingleObject(process,2000);CloseHandle(process);process=nullptr;}if(pipe){CloseHandle(pipe);pipe=nullptr;}}
    void read(void* out,size_t size) {
        auto data=(uint8_t*)out;
        while(size){DWORD got=0;if(!ReadFile(pipe,data,(DWORD)std::min<size_t>(size,1<<20),&got,nullptr)||!got)throw std::runtime_error("DSD 解码进程中断或文件损坏");data+=got;size-=got;}
    }
    void start(const std::wstring& file,int track,bool pcm,int rate,double position) {
        SECURITY_ATTRIBUTES sa{sizeof(sa),nullptr,TRUE};HANDLE writer=nullptr;
        if(!CreatePipe(&pipe,&writer,&sa,0))throw std::runtime_error("无法建立 DSD 数据管道");
        SetHandleInformation(pipe,HANDLE_FLAG_INHERIT,0);
        auto exe=(appDir/L"LumaDsd.exe").wstring();
        std::wstring cmd=L"\""+exe+L"\" --stream \""+file+L"\" "+std::to_wstring(track)+(pcm?L" pcm ":L" raw ")+std::to_wstring(rate)+L" "+std::to_wstring(position);
        STARTUPINFOW si{sizeof(si)};si.dwFlags=STARTF_USESTDHANDLES;si.hStdOutput=writer;si.hStdError=GetStdHandle(STD_ERROR_HANDLE);si.hStdInput=GetStdHandle(STD_INPUT_HANDLE);
        PROCESS_INFORMATION pi{};
        bool ok=CreateProcessW(exe.c_str(),cmd.data(),nullptr,nullptr,TRUE,CREATE_NO_WINDOW,nullptr,appDir.c_str(),&si,&pi);
        CloseHandle(writer);
        if(!ok)throw std::runtime_error("无法启动 LumaDsd.exe");
        process=pi.hProcess;CloseHandle(pi.hThread);
        auto deadline=GetTickCount64()+15000;
        while(true){DWORD available=0;if(!PeekNamedPipe(pipe,nullptr,0,nullptr,&available,nullptr))throw std::runtime_error("DSD 文件无法解码");if(available>=sizeof(header))break;if(GetTickCount64()>deadline)throw std::runtime_error("DSD 解码启动超时");Sleep(5);}
        read(&header,sizeof(header));
        if(header.magic!=0x414d554c||header.version!=1||header.channels<1||header.channels>8||header.rate<1)throw std::runtime_error("DSD 解码协议无效");
    }
};

struct Engine {
    std::wstring file; int track=1,backend=1,device=-1,dsdMode=0,pcmRate=176400,forceRate=0;
    bool downmix=false,dsd=false,wasapi=false,asio=false;
    HSTREAM source=0,mixer=0;
    std::unique_ptr<Worker> worker;
    uint32_t sourceRate=0,channels=0,rate=0,sourceChannels=0,bits=0;
    double duration=0,offset=0,latency=0;
    // cue 拆轨的播放上限（绝对秒）。<=0 表示整轨播放。
    double limit=0;
    // cue 曲目的固定起点，与续播/定位后的解码位置 offset 分开保存。
    double origin=0;
    std::vector<uint8_t> ring=std::vector<uint8_t>(16*1024*1024);
    std::atomic<uint64_t> head{0},tail{0},played{0},underruns{0};
    std::atomic<bool> quit{false},ended{false},failed{false},playing{false};
    std::atomic<float> volume{0.8f};
    std::thread producer;
    std::string failure;
    std::vector<uint8_t> pending;size_t pendingOffset=0;
    int mapping[8]{0,1,2,3,4,5,6,7};
    bool dopMarker=false;
    // ReplayGain 音量修正（线性倍率）。只乘在 PCM 浮点样本上，
    // 不重采样、不改 DSD 字节流，所以不破坏位完美回放路径。
    float gainLinear=1.f;
    // 可视化频谱：从解码出来的 PCM 上取样，不碰送往声卡的缓冲，也不在设备回调里算。
    // 独占模式下网页那一路已经不再送往扬声器，频谱只能从这里来。
    std::array<float,SPECTRUM_FFT> window{};
    unsigned windowFill=0;
    uint64_t lastSpectrumMs=0;
    // 这一帧频谱对应的样本率：PCM 是输出率，DSD 是解调后的率，前端按它算 bin 宽。
    uint32_t spectrumInputRate=0;
    // 最近一帧，0..255，和 AnalyserNode.getByteFrequencyData 同量纲。
    // 由解码线程写、luma_spectrum 在主线程读，两边不共用 gate，所以这一份单独加锁。
    std::array<uint8_t,SPECTRUM_BINS> spectrum{};
    std::mutex spectrumMutex;
    float smoothed[SPECTRUM_BINS]{};
    std::atomic<bool> spectrumValid{false};
    // 这一帧对应的采样率，前端用它算每个 bin 的频宽。
    std::atomic<uint32_t> spectrumRate{0};
    // DSD 解调状态：一阶 Sigma-Delta，窗口按位计。位流按「每组 dsdGroupChannels 个字节」交错
    // （每声道每字节 8 个 DSD 位），各声道严格同步推进，所以一个计数器就够。
    // 分组用的是源声道数而不是输出声道数：解调读的是位流本来的交错方式。
    uint32_t dsdWindowBytes=0;
    uint32_t dsdGroupChannels=0;
    uint32_t dsdFilled=0;
    std::vector<uint32_t> dsdChannelOnes;

    /** 推进频谱滑窗；攒满一窗且到时间了就重算一帧。PCM 与 DSD 解调共用。 */
    void pushSpectrumSample(float sample){
        window[windowFill++]=sample;
        if(windowFill<SPECTRUM_FFT)return;
        windowFill=0;
        uint64_t now=GetTickCount64();
        if(now-lastSpectrumMs<SPECTRUM_INTERVAL_MS)return;
        lastSpectrumMs=now;
        computeSpectrum();
    }
    // 只在 produce() 线程里调用：把刚解出的 float PCM 混成单声道填进滑动窗。
    void feedSpectrum(const float* samples,size_t frames){
        if(dsd&&dsdMode!=0)return;
        if(!channels||!frames)return;
        spectrumInputRate=rate;
        for(size_t f=0;f<frames;++f){
            float sum=0.f;
            for(unsigned c=0;c<channels;++c)sum+=samples[f*channels+c];
            pushSpectrumSample(sum/(float)channels);
        }
    }
    /**
     * DSD 位流解调后再喂频谱。
     *
     * 1-bit 噪声整形码流没有能把 FFT 直接吃下去的浮点样本，所以取窗口内 1 的比例：
     * 比例 2x-1 就是该时刻的模拟电平，这是最省的一阶 Sigma-Delta 解调。窗口长按源位率
     * 除到约 44.1k 定：DSD64（2.8224M）取 64 位 = 8 字节，DSD128 取 128 位，依次类推。
     *
     * 取样点必须在这里、在 push() 与 pack_dop() 之前：只读不改，位完美透传路径不受影响。
     */
    void feedDsdSpectrum(const uint8_t* data,size_t length){
        if(!(dsd&&dsdMode!=0)||!dsdWindowBytes||!dsdGroupChannels||!length)return;
        const size_t groups=length/dsdGroupChannels;
        if(!groups)return;
        spectrumInputRate=(uint32_t)std::llround((double)sourceRate/((double)dsdWindowBytes*8.0));
        const double total=(double)dsdWindowBytes*8.0;
        for(size_t g=0;g<groups;++g){
            const uint8_t* group=data+g*dsdGroupChannels;
            for(unsigned c=0;c<dsdGroupChannels;++c)dsdChannelOnes[c]+=popcount8(group[c]);
            if(++dsdFilled<dsdWindowBytes)continue;
            // 一个窗口满：每声道 1 的比例映射到 [-1,1]，再按声道平均成单声道。
            float sum=0.f;
            for(unsigned c=0;c<dsdGroupChannels;++c){
                sum+=(float)(2.0*(double)dsdChannelOnes[c]/total-1.0);
                dsdChannelOnes[c]=0;
            }
            dsdFilled=0;
            pushSpectrumSample(sum/(float)dsdGroupChannels);
        }
    }
    /** 按源位率把解调窗口定到约 44.1k；非 DSD 透传时清空状态。 */
    void setupDsdSpectrum(){
        dsdWindowBytes=0;dsdGroupChannels=0;dsdFilled=0;dsdChannelOnes.clear();
        if(!(dsd&&dsdMode!=0)||!sourceRate||!channels||!sourceChannels)return;
        // 解调必须按源位流的交错单位切字节。两者目前总相等（DSD 透传既不允许共享模式，也不会
        // downmix），但不相等时宁可一帧都不出，也不要给出一帧错位却看起来「有反应」的频谱。
        if(sourceChannels!=channels)return;
        dsdGroupChannels=sourceChannels;
        dsdChannelOnes.assign(dsdGroupChannels,0);
        // 每窗口位数取 8 的整数倍，保证正好按字节消费。
        unsigned bits=(unsigned)std::llround((double)sourceRate/SPECTRUM_DSD_RATE/8.0)*8;
        if(bits<SPECTRUM_DSD_MIN_BITS)bits=SPECTRUM_DSD_MIN_BITS;
        if(bits>SPECTRUM_DSD_MAX_BITS)bits=SPECTRUM_DSD_MAX_BITS;
        dsdWindowBytes=bits/8;
    }
    void computeSpectrum(){
        std::vector<float> re(window.begin(),window.end());
        std::vector<float> im(SPECTRUM_FFT,0.f);
        // Blackman 窗，和 AnalyserNode 内部用的窗一致，减少截断造成的频谱泄漏。
        for(unsigned i=0;i<SPECTRUM_FFT;++i){
            double phase=2.0*3.14159265358979323846*(double)i/(double)(SPECTRUM_FFT-1);
            double w=0.42-0.5*std::cos(phase)+0.08*std::cos(2.0*phase);
            re[i]=(float)(re[i]*w);
        }
        spectrumFft(re,im);
        std::array<uint8_t,SPECTRUM_BINS> frame{};
        for(unsigned bin=0;bin<SPECTRUM_BINS;++bin){
            // 幅度转 dBFS 再映射到 0..255，公式与 AnalyserNode 的 getByteFrequencyData 一致。
            double magnitude=std::sqrt((double)re[bin]*re[bin]+(double)im[bin]*im[bin])/(double)SPECTRUM_FFT;
            if(magnitude<1e-12)magnitude=1e-12;
            double db=20.0*std::log10(magnitude);
            double normalized=(db-SPECTRUM_MIN_DB)/(SPECTRUM_MAX_DB-SPECTRUM_MIN_DB);
            normalized=std::clamp(normalized,0.0,1.0);
            float value=(float)normalized*255.f;
            // 平滑按上一帧的值推进，避免频段跳变。
            smoothed[bin]=SPECTRUM_SMOOTHING*smoothed[bin]+(1.f-SPECTRUM_SMOOTHING)*value;
            float out=smoothed[bin];
            frame[bin]=(uint8_t)(out<0.f?0:out>255.f?255:out);
        }
        spectrumRate.store(spectrumInputRate?spectrumInputRate:rate,std::memory_order_relaxed);
        {
            std::lock_guard frameLock(spectrumMutex);
            spectrum=frame;
        }
        spectrumValid.store(true,std::memory_order_release);
    }
    void resetSpectrum(){
        windowFill=0;
        lastSpectrumMs=0;
        dsdFilled=0;
        for(uint32_t& ones:dsdChannelOnes)ones=0;
        for(float& v:smoothed)v=0.f;
        {
            std::lock_guard frameLock(spectrumMutex);
            spectrum.fill(0);
        }
        spectrumValid.store(false,std::memory_order_release);
    }
    // 输出设备被系统移除（拔 USB DAC / 驱动重置），与解码失败区分开以便自动恢复。
    std::atomic<bool> deviceGone{false};
    // 设备实际协商出的位深；0 表示未知或 DSD 透传（那里没有 PCM 位深可言）。
    int deviceBits=0;
    // 引擎侧淡入淡出：网页那一路在接管后被静音，淡入淡出只能在这里做。
    // 0..1 的当前增益，按每次回调的样本数逐点推进，避免开关处出现爆音。
    std::atomic<float> fade{1.f};
    std::atomic<float> fadeTarget{1.f};
    // 每帧需要的秒数由 rate 决定；pull() 里按实际样本推进。
    std::atomic<float> fadeSeconds{0.25f};
    // 每个样本走一步，避免一次回调一个台阶产生的咔哒声。
    void applyFade(float* samples,size_t frames){
        float target=fadeTarget.load();
        float current=fade.load();
        if(current==target&&target==1.f)return;
        float seconds=fadeSeconds.load();
        if(!(seconds>0.f)){fade.store(target);if(target!=1.f)for(size_t i=0;i<frames*channels;++i)samples[i]*=target;return;}
        float perFrame=1.f/(seconds*(float)rate);
        for(size_t frame=0;frame<frames;++frame){
            if(current<target)current=std::min(target,current+perFrame);else if(current>target)current=std::max(target,current-perFrame);
            float* sample=samples+frame*channels;
            for(unsigned c=0;c<channels;++c)sample[c]*=current;
        }
        fade.store(current);
    }
    ~Engine(){stop();}
    // 只有已初始化的后端才需要重新选设备；另外 WASAPI 的设备是按索引的，
    // 这里补上失败的一次性重试（驱动切换瞬间常见）。
    void activate(){
        if(wasapi){for(int i=0;i<2;++i){if(BASS_WASAPI_SetDevice(device))break;Sleep(10);}}
        if(asio){for(int i=0;i<2;++i){if(BASS_ASIO_SetDevice(device))break;Sleep(10);}}
    }
    void stop(){
        playing=false;quit=true;
        activate();
        if(wasapi)BASS_WASAPI_SetNotify(nullptr,nullptr);
        if(asio)BASS_ASIO_SetNotify(nullptr,nullptr);
        if(wasapi){BASS_WASAPI_Stop(TRUE);BASS_WASAPI_Free();wasapi=false;}
        if(asio){BASS_ASIO_Stop();BASS_ASIO_Free();asio=false;}
        // Kill a blocked pipe producer before joining it.
        if(worker)worker->cancel();
        if(producer.joinable())producer.join();
        if(mixer){BASS_StreamFree(mixer);mixer=0;}if(source){BASS_StreamFree(source);source=0;}
        worker.reset();
    }
    size_t frameSize()const{return channels*(dsd&&dsdMode==2?1:4);}
    size_t push(const uint8_t* data,size_t length){
        size_t done=0;
        while(done<length&&!quit){auto h=head.load(std::memory_order_relaxed);auto t=tail.load(std::memory_order_acquire);size_t room=ring.size()-(size_t)(h-t);if(!room){Sleep(2);continue;}auto n=std::min({room,length-done,ring.size()-(size_t)(h%ring.size())});memcpy(ring.data()+h%ring.size(),data+done,n);head.store(h+n,std::memory_order_release);done+=n;}
        return done;
    }
    DWORD pull(void* buffer,DWORD length){
        auto t=tail.load(std::memory_order_relaxed);auto h=head.load(std::memory_order_acquire);
        size_t n=std::min<size_t>(length,h-t);n-=n%frameSize();
        // cue 曲目只放到 limit 为止：截断后按静音补齐，绝不越过下一首的起点。
        // 截断不是欠载（欠载会拉高 underruns 计数、污染 UI 回归的断言），所以单独标记。
        bool truncated=false;
        if(limit>0){
            double bytesPerSecond=(double)rate*frameSize()/(dsd&&dsdMode==2?8:1);
            uint64_t total=(uint64_t)std::llround(std::max(0.0,(playable()-offset)*bytesPerSecond)/frameSize())*frameSize();
            auto consumed=played.load();
            uint64_t budget=consumed<total?total-consumed:0;
            if(budget<length)truncated=true;
            if(n>budget)n=(size_t)budget;
        }
        auto first=std::min(n,ring.size()-(size_t)(t%ring.size()));memcpy(buffer,ring.data()+t%ring.size(),first);if(n>first)memcpy((uint8_t*)buffer+first,ring.data(),n-first);
        tail.store(t+n,std::memory_order_release);played+=n;
        if(n<length){
            if(!ended&&!quit&&!truncated)underruns++;
            if(dsd&&dsdMode==2)memset((uint8_t*)buffer+n,0x69,length-n);
            else if(dsd&&dsdMode==1){
                // Derive marker continuation from last valid frame, including underrun boundaries.
                bool marker=false;if(n>=frameSize()){float sample=((float*)buffer)[n/4-channels];int v=(int)std::lrint(sample*8388608.f);marker=((v>>16)&255)==0x05;}
                dop_silence((float*)((uint8_t*)buffer+n),(length-n)/frameSize(),channels,marker);
            }else memset((uint8_t*)buffer+n,0,length-n);
        }
        // ReplayGain 折进软件音量：DSD 透传/原生路径不适用（那里没有浮点样本可乘），
        // 这是位完美的正确行为，不该绕过。
        if(!(dsd&&dsdMode!=0)){auto samples=(float*)buffer;float g=volume.load()*gainLinear;if(g!=1.f)for(size_t i=0;i<n/4;++i)samples[i]*=g;}
        // 淡入淡出只作用于 PCM 浮点样本：DSD 是位流，乘系数会破坏它。
        if(!(dsd&&dsdMode!=0)){
            int frames=(int)(n/(channels*4));
            applyFade((float*)buffer,(size_t)frames);
        }
        return length;
    }
    static DWORD CALLBACK wasapiProc(void* p,DWORD n,void* user){return ((Engine*)user)->pull(p,n);}
    static DWORD CALLBACK asioProc(BOOL,DWORD,void* p,DWORD n,void* user){return ((Engine*)user)->pull(p,n);}
    static void CALLBACK wasapiNotify(DWORD reason,DWORD dev,void* user){auto e=(Engine*)user;if((int)dev==e->device&&(reason==BASS_WASAPI_NOTIFY_DISABLED||reason==BASS_WASAPI_NOTIFY_FAIL)){e->deviceGone=true;e->failed=true;}}
    static void CALLBACK asioNotify(DWORD,void* user){((Engine*)user)->failed=true;}
    bool nextBlock(){uint32_t n=0;worker->read(&n,4);if(!n)return false;if(n>16*1024*1024)throw std::runtime_error("无效 DSD 数据块");pending.resize(n);worker->read(pending.data(),n);pendingOffset=0;return true;}
    static DWORD CALLBACK pcmRead(HSTREAM,void* out,DWORD length,void* user){
        auto e=(Engine*)user;size_t count=0;
        try{while(count<length&&!e->quit){if(e->pendingOffset>=e->pending.size()&&!e->nextBlock())break;auto n=std::min<size_t>(length-count,e->pending.size()-e->pendingOffset);memcpy((uint8_t*)out+count,e->pending.data()+e->pendingOffset,n);e->pendingOffset+=n;count+=n;}}
        catch(const std::exception& ex){e->failure=ex.what();e->failed=true;}
        return (DWORD)count | (count<length?BASS_STREAMPROC_END:0);
    }
    void produce(){
        try{
            if(dsd&&dsdMode!=0){
                std::vector<uint8_t> carry;std::vector<float> dop;
                while(!quit&&nextBlock()){
                    // 频谱在变成 DoP 浮点/裸位流之前取：这里是真正的 DSD 位流，解调后可读；
                    // 而 push() 出去的那份（DoP 伪样本或原始字节）对 FFT 没有意义。
                    feedDsdSpectrum(pending.data(),pending.size());
                    if(dsdMode==2){push(pending.data(),pending.size());continue;}
                    carry.insert(carry.end(),pending.begin(),pending.end());auto frames=carry.size()/(channels*2);
                    dop.resize(frames*channels);pack_dop(carry.data(),dop.data(),frames,channels,dopMarker);push((uint8_t*)dop.data(),dop.size()*4);
                    carry.erase(carry.begin(),carry.begin()+frames*channels*2);
                }
            }else{
                std::vector<uint8_t> data(65536-65536%frameSize());
                while(!quit){DWORD n=BASS_ChannelGetData(mixer?mixer:source,data.data(),(DWORD)data.size());if(n==(DWORD)-1){if(BASS_ErrorGetCode()!=BASS_ERROR_ENDED)throw std::runtime_error("音频解码失败");break;}if(!n)break;
                    // 频谱取在把这段字节交给环形缓冲之前：这里拿到的是解码器刚产出的浮点样本，
                    // 还没乘音量和淡化，也不会和 pull() 抢同一个解码流。
                    // DSD 转 PCM 也走这条，此时样本同样是浮点，可以正常取频谱。
                    feedSpectrum((const float*)data.data(),n/(channels*4));
                    push(data.data(),n);}
            }
        }catch(const std::exception& ex){if(!quit){failure=ex.what();failed=true;}}
        ended=true;
    }
    void open(double position){
        offset=position;auto ext=fs::path(file).extension().wstring();std::transform(ext.begin(),ext.end(),ext.begin(),towlower);
        dsd=ext==L".dsf"||ext==L".dff"||ext==L".iso";
        if(dsd&&dsdMode==3)throw std::runtime_error("请选择 DSD 输出方式：原生 DSD、DoP 或转 PCM。");
        if(dsd&&dsdMode==2&&backend!=2)throw std::runtime_error("原生 DSD 需要选择 ASIO 厂商驱动。");
        if(dsd&&dsdMode==1&&backend==0)throw std::runtime_error("DoP 需要 WASAPI 独占或 ASIO。");
        if(dsd){
            worker=std::make_unique<Worker>();worker->start(file,track,dsdMode==0,pcmRate,position);
            auto info=worker->header;sourceRate=info.sourceRate;sourceChannels=info.channels;duration=info.duration;bits=1;
            channels=sourceChannels;rate=dsdMode==1?sourceRate/16:dsdMode==2?sourceRate:pcmRate;
            if(dsdMode==0)source=BASS_StreamCreate(rate,channels,BASS_SAMPLE_FLOAT|BASS_STREAM_DECODE,pcmRead,this);
        }else{
            source=BASS_StreamCreateFile(FALSE,file.c_str(),0,0,BASS_UNICODE|BASS_SAMPLE_FLOAT|BASS_STREAM_DECODE|BASS_ASYNCFILE);
            if(!source)throw std::runtime_error("无法解码文件（BASS "+std::to_string(BASS_ErrorGetCode())+"）"+(pluginWarning.empty()?"":"；"+pluginWarning));
            BASS_CHANNELINFO info{};BASS_ChannelGetInfo(source,&info);sourceRate=rate=info.freq;sourceChannels=channels=info.chans;bits=info.origres;
            duration=BASS_ChannelBytes2Seconds(source,BASS_ChannelGetLength(source,BASS_POS_BYTE));
            if(position>0&&!BASS_ChannelSetPosition(source,BASS_ChannelSeconds2Bytes(source,position),BASS_POS_BYTE))throw std::runtime_error("定位失败");
        }
        if(channels>8||!channels)throw std::runtime_error("最多支持 8 个输出声道");
        if(backend==0){
            BASS_WASAPI_DEVICEINFO info{};if(!BASS_WASAPI_GetDeviceInfo(device,&info))throw std::runtime_error("音频设备已不可用");
            // DSD 的 DoP/原生通道有自己固定的率：共享模式的设备混合率会把它们覆盖成
            // 44.1/48k，输出就不再是 DSD。DoP 本来也不允许走共享（上面已拦）。
            if(dsd&&dsdMode!=0){
                if(channels>info.mixchans&&!downmix)throw std::runtime_error("设备声道不足，请确认混成立体声后重试。");
                if(downmix)channels=2;
            }else{
                rate=info.mixfreq;if(channels>info.mixchans&&!downmix)throw std::runtime_error("设备声道不足，请确认混成立体声后重试。");
                channels=info.mixchans;
            }
        }else if(!(dsd&&dsdMode!=0)){
            // WASAPI 独占先试请求率，设备不接受再退回源采样率；ASIO 在 Init 后回退。
            // 实际生效值经 luma_state 上报，设置页和日志里都看得到。
            if(forceRate&&backend==1&&forceRate!=rate){
                if(BASS_WASAPI_CheckFormat(device,forceRate,channels,BASS_WASAPI_EXCLUSIVE|BASS_WASAPI_EVENT)!=(DWORD)-1)rate=forceRate;
            }
            if(downmix)channels=2;
        }
        if(!(dsd&&dsdMode!=0)){
            mixer=BASS_Mixer_StreamCreate(rate,channels,BASS_SAMPLE_FLOAT|BASS_STREAM_DECODE|BASS_MIXER_END);
            if(!mixer||!BASS_Mixer_StreamAddChannel(mixer,source,BASS_MIXER_CHAN_DOWNMIX|BASS_MIXER_CHAN_NORAMPIN))throw std::runtime_error("无法配置 PCM 转换链路");
        }
        if(backend==2){
            // 上一会话可能还没被驱动真正回收：ALREADY 时沿用现有会话，但必须重新选设备并
            // 重新设率，否则 SetRate 会作用在旧设备/旧状态上，表现为"切了采样率不生效"。
            if(!BASS_ASIO_Init(device,BASS_ASIO_THREAD|BASS_ASIO_JOINORDER)){
                if(BASS_ASIO_ErrorGetCode()!=BASS_ERROR_ALREADY)throw std::runtime_error("ASIO 驱动无法打开："+std::to_string(BASS_ASIO_ErrorGetCode()));
                BASS_ASIO_SetDevice(device);
            }
            asio=true;
            BASS_ASIO_INFO info{};BASS_ASIO_GetInfo(&info);
            for(unsigned c=0;c<channels;++c)if(mapping[c]<0||mapping[c]>=(int)info.outputs)throw std::runtime_error("ASIO 输出通道不足或映射无效");
            if(dsd&&dsdMode==2&&!BASS_ASIO_SetDSD(TRUE))throw std::runtime_error("当前 ASIO 驱动不支持原生 DSD");
            // ASIO 支持哪些率只有 Init 之后才能查询：请求率可用就锁过去并重建混音器，
            // 不可可用则维持源采样率（混音器建时就已是源率，无需重建）。
            if(forceRate&&!(dsd&&dsdMode!=0)&&forceRate!=rate&&BASS_ASIO_CheckRate(forceRate)){
                rate=forceRate;
                mixer=BASS_Mixer_StreamCreate(rate,channels,BASS_SAMPLE_FLOAT|BASS_STREAM_DECODE|BASS_MIXER_END);
                if(!mixer||!BASS_Mixer_StreamAddChannel(mixer,source,BASS_MIXER_CHAN_DOWNMIX|BASS_MIXER_CHAN_NORAMPIN))throw std::runtime_error("无法配置 PCM 转换链路");
            }
            if(!BASS_ASIO_CheckRate(rate)||!BASS_ASIO_SetRate(rate))throw std::runtime_error("设备不支持当前采样率，请修改输出格式");
            // 个别驱动会接受 SetRate 却仍按自己的率跑：以驱动回报的率为准，
            // 否则界面显示 384k 而硬件实际还是 48k，等于在骗人。
            {
                double actual=BASS_ASIO_GetRate();
                if(actual>0&&std::fabs(actual-(double)rate)>0.5)rate=(uint32_t)std::llround(actual);
            }
            int first=mapping[0];
            if(!BASS_ASIO_ChannelEnable(FALSE,first,asioProc,this))throw std::runtime_error("ASIO 通道启用失败");
            for(unsigned c=1;c<channels;++c)if(!BASS_ASIO_ChannelJoin(FALSE,mapping[c],first))throw std::runtime_error("ASIO 通道映射失败");
            if(!BASS_ASIO_ChannelSetFormat(FALSE,first,dsd&&dsdMode==2?BASS_ASIO_FORMAT_DSD_MSB:BASS_ASIO_FORMAT_FLOAT))throw std::runtime_error("ASIO 数据格式不受支持");
            BASS_ASIO_SetNotify(asioNotify,this);latency=(double)BASS_ASIO_GetLatency(FALSE)/(dsd&&dsdMode==2?rate/8:rate);
            if(!(dsd&&dsdMode==2))deviceBits=asioFormatBits(BASS_ASIO_ChannelGetFormat(FALSE,first));
        }else{
            DWORD flags=backend==1?BASS_WASAPI_EXCLUSIVE|BASS_WASAPI_EVENT:0;
            if(BASS_WASAPI_CheckFormat(device,rate,channels,flags)==(DWORD)-1)throw std::runtime_error("设备不支持此采样率或声道，请选择兼容格式");
            // 独占请求偶尔撞上音频引擎正在切换的窗口，短暂重试两次；仍 BUSY 则是真实占用。
            bool opened=false;for(int attempt=0;attempt<3&&!opened;++attempt){
                if(attempt)Sleep(300);
                if(BASS_WASAPI_Init(device,rate,channels,flags,0.15f,0,wasapiProc,this))opened=true;
                else if(BASS_ErrorGetCode()!=BASS_ERROR_BUSY)break;
            }
            if(!opened){
                DWORD code=BASS_ErrorGetCode();
                if(code==BASS_ERROR_BUSY)throw std::runtime_error("音频设备被其他程序占用，或存在残留的独占会话（46）。\n可切换共享模式播放；或重启 Windows 音频服务/重新插拔设备后恢复独占。");
                throw std::runtime_error("无法取得音频设备（"+std::to_string(code)+"）");
            }
            wasapi=true;BASS_WASAPI_INFO info{};BASS_WASAPI_GetInfo(&info);
            deviceBits=wasapiFormatBits(info.format);
            if(dsd&&dsdMode==1&&(info.format==BASS_WASAPI_FORMAT_8BIT||info.format==BASS_WASAPI_FORMAT_16BIT))throw std::runtime_error("DoP 输出至少需要 24 位设备格式");
            latency=(double)info.buflen/(rate*channels*4);BASS_WASAPI_SetNotify(wasapiNotify,this);
        }
        // 声道数到这里才最终定下（共享模式会改成设备混音声道，downmix 会降到 2），
        // 所以 DSD 解调窗口要在这之后、起解码线程之前准备好。
        setupDsdSpectrum();
        producer=std::thread([this]{produce();});
        auto deadline=GetTickCount64()+15000;
        while(head<rate*frameSize()/(dsd&&dsdMode==2?80:10)&&!ended&&!failed){if(GetTickCount64()>deadline)throw std::runtime_error("音频预缓冲超时");Sleep(2);}
        if(failed)throw std::runtime_error(failure.empty()?"设备初始化失败":failure);
    }
    void resume(){activate();bool ok=asio?BASS_ASIO_Start(0,0):BASS_WASAPI_Start();if(!ok)throw std::runtime_error("音频设备启动失败");playing=true;}
    // 真 seek：只重建解码器，不释放也不重新 acquire 输出设备。
    // 独占模式下整首重开会让设备被别的程序抢走，拖进度条尤其明显。
    void seek(double target){
        if(!std::isfinite(target))throw std::runtime_error("无效的播放位置");
        double span=playable();
        target=std::clamp(target,origin,std::max(origin,span-.02));
        if(quit||failed)throw std::runtime_error("播放已停止，无法定位");
        quit=true;
        if(worker)worker->cancel();
        if(producer.joinable())producer.join();
        // 丢掉上一段残留数据，重新对齐读写指针；否则会把 seek 前后的音频拼在一起。
        head.store(0);tail.store(0);played.store(0);ended.store(false);underruns.store(0);
        pending.clear();pendingOffset=0;pending.resize(0);
        dopMarker=false;
        resetSpectrum();
        quit=false;
        offset=target;
        if(dsd){
            // DSD 管道不可 seek：只能重启解码工作进程从目标位置重扫（DST 会比较慢）。
            worker=std::make_unique<Worker>();worker->start(file,track,dsdMode==0,pcmRate,target);
            if(dsdMode==0&&source){BASS_StreamFree(source);source=BASS_StreamCreate(rate,channels,BASS_SAMPLE_FLOAT|BASS_STREAM_DECODE,pcmRead,this);}
        }else{
            if(!BASS_ChannelSetPosition(source,BASS_ChannelSeconds2Bytes(source,target),BASS_POS_BYTE))
                throw std::runtime_error("定位失败");
        }
        producer=std::thread([this]{produce();});
        auto deadline=GetTickCount64()+15000;
        size_t want=(size_t)(rate*frameSize()/(dsd&&dsdMode==2?80:10));
        while(head<want&&!ended&&!failed){if(GetTickCount64()>deadline)throw std::runtime_error("音频预缓冲超时");Sleep(2);}
        if(failed)throw std::runtime_error(failure.empty()?"定位后无法继续播放":failure);
    }
    // cue 曲目的可播时长：limit 是绝对秒，<=0 时取整轨长度。
    double playable()const{return limit>0?std::min(limit,duration):duration;}
    // 进度条长度：cue 曲目只覆盖自己那一段，不是整轨长度。
    double span()const{return playable()-origin;}
    bool atLimit()const{
        if(limit<=0)return false;
        double bytesPerSecond=(double)rate*frameSize()/(dsd&&dsdMode==2?8:1);
        uint64_t total=(uint64_t)std::llround(std::max(0.0,(playable()-offset)*bytesPerSecond)/frameSize())*frameSize();
        return played.load()>=total;
    }
    // position 相对播放起点上报，UI 才能把 cue 曲目的进度条当成 0→Duration。
    double position()const{double bytesPerSecond=(double)rate*frameSize()/(dsd&&dsdMode==2?8:1);double raw=offset+played.load()/bytesPerSecond-(playing?latency:0);return std::clamp(raw-origin,0.0,span());}
};
static std::unique_ptr<Engine> engine;

API int luma_init(const wchar_t* directory){std::lock_guard lock(gate);try{appDir=directory;SetDllDirectoryW(directory);BASS_SetConfig(BASS_CONFIG_UNICODE,TRUE);if(!BASS_Init(0,44100,0,nullptr,nullptr)&&BASS_ErrorGetCode()!=BASS_ERROR_ALREADY)throw std::runtime_error("BASS 初始化失败");
    // 插件缺失时 BASS 只会在播放那一步报“无法解码”，把缺的 DLL 名字留在这里更像实话。
    std::string missing;
    for(auto name:{L"bassflac.dll",L"bassape.dll",L"bassalac.dll",L"bassopus.dll",L"basswv.dll",L"bassdsd.dll"}){
        auto p=BASS_PluginLoad((appDir/name).c_str(),BASS_UNICODE);
        if(p)plugins.push_back(p);
        else missing+=(missing.empty()?"":"、")+utf8(name);
    }
    if(!missing.empty())pluginWarning="缺少解码插件："+missing;
    initialized=true;return 1;}catch(const std::exception& e){errorText=e.what();return 0;}}
API const char* luma_error(){std::lock_guard lock(gate);resultText=errorText;return resultText.c_str();}
API const char* luma_devices(){std::lock_guard lock(gate);std::ostringstream o;o<<"[";bool comma=false;BASS_WASAPI_DEVICEINFO d{};for(int i=0;BASS_WASAPI_GetDeviceInfo(i,&d);++i){if(!(d.flags&BASS_DEVICE_ENABLED)||(d.flags&(BASS_DEVICE_INPUT|BASS_DEVICE_LOOPBACK)))continue;if(comma)o<<",";comma=true;o<<"{\"index\":"<<i<<",\"backend\":1,\"name\":"<<quote(d.name?d.name:"")<<",\"id\":"<<quote(d.id?d.id:"")<<",\"default\":"<<((d.flags&BASS_DEVICE_DEFAULT)?"true":"false")<<",\"channels\":"<<d.mixchans<<",\"rate\":"<<d.mixfreq<<"}";}BASS_ASIO_DEVICEINFO a{};for(int i=0;BASS_ASIO_GetDeviceInfo(i,&a);++i){if(comma)o<<",";comma=true;o<<"{\"index\":"<<i<<",\"backend\":2,\"name\":"<<quote(a.name?a.name:"")<<",\"id\":"<<quote(a.driver?a.driver:"")<<",\"default\":false,\"channels\":0,\"rate\":0}";}o<<"]";resultText=o.str();return resultText.c_str();}
// 每次新开都从静音淡入：上一首暂停/停止后增益会停在 0，不重置就是整首无声。
API int luma_open(const wchar_t* file,int track,int backend,int device,int mode,int pcmRate,int forceRate,int downmix,const int* map,float volume,double position,double limit,float gainLinear,double cueStart,double fadeIn){std::lock_guard lock(gate);try{engine.reset();auto next=std::make_unique<Engine>();next->file=file;next->track=track;next->backend=backend;next->device=device;next->dsdMode=mode;next->pcmRate=pcmRate;next->forceRate=forceRate;next->downmix=downmix!=0;next->volume=volume;next->gainLinear=(std::isfinite(gainLinear)&&gainLinear>0)?std::clamp(gainLinear,0.01f,4.f):1.f;next->limit=std::isfinite(limit)?limit:0;next->origin=next->limit>0&&std::isfinite(cueStart)?std::clamp(cueStart,0.0,next->limit):0;if(map){for(int i=0;i<8;++i){next->mapping[i]=map[i];for(int j=0;j<i;++j)if(map[i]==map[j])throw std::runtime_error("通道映射不能重复");}}next->open(position);next->fadeSeconds=std::clamp((float)(std::isfinite(fadeIn)?fadeIn:0.0),0.f,5.f);engine=std::move(next);engine->resume();errorText.clear();return 1;}catch(const std::exception& e){errorText=e.what();engine.reset();return 0;}}
API int luma_pause(int pause){std::lock_guard lock(gate);try{if(!engine)return 0;engine->activate();if(pause){if(engine->asio)BASS_ASIO_Stop();else BASS_WASAPI_Stop(FALSE);engine->playing=false;}else engine->resume();return 1;}catch(const std::exception&e){errorText=e.what();return 0;}}
// 淡入淡出：目标增益 0..1，秒数为渐变时长；PCM 路径逐样本推进，DSD 位流不参与。
API int luma_fade(float target,double seconds){std::lock_guard lock(gate);if(!engine)return 0;engine->fadeTarget=std::clamp(target,0.f,1.f);engine->fadeSeconds=std::clamp((float)seconds,0.f,5.f);return 1;}
// 暂停前先淡出到 0：阻塞到渐变结束，随后的 Stop 才不会在满幅处切断。
API int luma_fade_wait(float target,double seconds){std::lock_guard lock(gate);if(!engine)return 0;engine->fadeTarget=std::clamp(target,0.f,1.f);engine->fadeSeconds=std::clamp((float)seconds,0.f,5.f);auto deadline=GetTickCount64()+(DWORD)(std::max(0.0,seconds)*1000+100);while(GetTickCount64()<deadline&&std::fabs(engine->fade.load()-engine->fadeTarget.load())>0.001f)Sleep(5);engine->fade.store(engine->fadeTarget.load());return 1;}
// 真 seek：保留已取得的输出设备（含独占句柄），只重建解码器。
// 暂停状态下也能定位，播放状态保持不变。
API int luma_seek(double position){std::lock_guard lock(gate);try{if(!engine)return 0;bool wasPlaying=engine->playing;if(wasPlaying){if(engine->asio)BASS_ASIO_Stop();else BASS_WASAPI_Stop(FALSE);}engine->seek(position);if(wasPlaying)engine->resume();errorText.clear();return 1;}catch(const std::exception&e){errorText=e.what();return 0;}}
// 停止后等驱动真正放手再返回：ASIO 和 WASAPI 独占的释放都是异步的，
// 立刻用新参数重开时会拿到旧会话（表现为切后端/切采样率后仍是旧值）。
API void luma_stop(){std::lock_guard lock(gate);if(!engine)return;const bool wasWasapi=engine->wasapi,wasAsio=engine->asio;engine.reset();for(int i=0;i<20;++i){bool busy=(wasWasapi&&BASS_WASAPI_IsStarted())||(wasAsio&&BASS_ASIO_IsStarted());if(!busy)break;Sleep(10);}Sleep(30);}
API void luma_volume(float v){std::lock_guard lock(gate);if(engine)engine->volume=std::clamp(v,0.f,1.f);}
// 可视化频谱：最近一帧 0..255 的 1024 个 bin，和网页 AnalyserNode 同量纲。
// DSD 透传走的是位流解调（feedDsdSpectrum），所以这里对 PCM 与 DSD 一视同仁。
//
// 返回 "<样本率> <1024 字节的 base64>"。没有引擎或还没攒出第一帧时返回空串，
// 前端据此保持上一帧，不伪造包络。
API const char* luma_spectrum(){
    std::lock_guard lock(gate);
    if(!engine||!engine->spectrumValid.load(std::memory_order_acquire)){
        resultText.clear();return resultText.c_str();
    }
    std::array<uint8_t,SPECTRUM_BINS> frame{};
    {
        std::lock_guard frameLock(engine->spectrumMutex);
        frame=engine->spectrum;
    }
    std::ostringstream o;
    o<<engine->spectrumRate.load(std::memory_order_relaxed)<<' '<<base64Bytes(frame.data(),frame.size());
    resultText=o.str();return resultText.c_str();
}
API const char* luma_state(){std::lock_guard lock(gate);if(!engine){resultText="{}";return resultText.c_str();}auto&e=*engine;
    // WASAPI/ASIO 的通知回调只置 failed/deviceGone，不写 failure。设备被拔掉或驱动重置时
    // 必须给出可读原因，否则设置页看不到故障，前端也无法说明为什么退回网页通路。
    const std::string failureText=e.failure.empty()&&e.failed?"输出设备已断开或音频驱动中断":e.failure;
    bool done=(e.ended&&e.head==e.tail)||e.atLimit();std::ostringstream o;o<<"{\"playing\":"<<(e.playing?"true":"false")<<",\"ended\":"<<(done?"true":"false")<<",\"failed\":"<<(e.failed?"true":"false")<<",\"error\":"<<quote(failureText)<<",\"position\":"<<e.position()<<",\"duration\":"<<e.span()<<",\"sourceRate\":"<<e.sourceRate<<",\"sourceChannels\":"<<e.sourceChannels<<",\"rate\":"<<e.rate<<",\"channels\":"<<e.channels<<",\"bits\":"<<e.bits<<",\"deviceBits\":"<<e.deviceBits<<",\"dsd\":"<<(e.dsd?"true":"false")<<",\"mode\":"<<e.dsdMode<<",\"backend\":"<<e.backend<<",\"underruns\":"<<e.underruns<<",\"gain\":"<<e.gainLinear<<",\"deviceGone\":"<<(e.deviceGone?"true":"false")<<"}";resultText=o.str();return resultText.c_str();}
API void luma_shutdown(){std::lock_guard lock(gate);engine.reset();for(auto p:plugins)BASS_PluginFree(p);plugins.clear();BASS_Free();initialized=false;}
// 测试入口：位深映射是纯函数，设备不在场也要能回归。
API int luma_format_bits(int api,DWORD format){return api==0?wasapiFormatBits(format):asioFormatBits(format);}
