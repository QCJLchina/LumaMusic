#include "common.h"
#include "audio_math.h"
#include "bass.h"
#include "basswasapi.h"
#include "bassasio.h"
#include "bassmix.h"
#include <filesystem>
#include <vector>
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
static bool initialized=false;
static thread_local std::string resultText;
static std::vector<HPLUGIN> plugins;

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
    std::vector<uint8_t> ring=std::vector<uint8_t>(16*1024*1024);
    std::atomic<uint64_t> head{0},tail{0},played{0},underruns{0};
    std::atomic<bool> quit{false},ended{false},failed{false},playing{false};
    std::atomic<float> volume{0.8f};
    std::thread producer;
    std::string failure;
    std::vector<uint8_t> pending;size_t pendingOffset=0;
    int mapping[8]{0,1,2,3,4,5,6,7};
    bool dopMarker=false;
    ~Engine(){stop();}
    void activate(){if(wasapi)BASS_WASAPI_SetDevice(device);if(asio)BASS_ASIO_SetDevice(device);}
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
        auto first=std::min(n,ring.size()-(size_t)(t%ring.size()));memcpy(buffer,ring.data()+t%ring.size(),first);if(n>first)memcpy((uint8_t*)buffer+first,ring.data(),n-first);
        tail.store(t+n,std::memory_order_release);played+=n;
        if(n<length){
            if(!ended&&!quit)underruns++;
            if(dsd&&dsdMode==2)memset((uint8_t*)buffer+n,0x69,length-n);
            else if(dsd&&dsdMode==1){
                // Derive marker continuation from last valid frame, including underrun boundaries.
                bool marker=false;if(n>=frameSize()){float sample=((float*)buffer)[n/4-channels];int v=(int)std::lrint(sample*8388608.f);marker=((v>>16)&255)==0x05;}
                dop_silence((float*)((uint8_t*)buffer+n),(length-n)/frameSize(),channels,marker);
            }else memset((uint8_t*)buffer+n,0,length-n);
        }
        if(!(dsd&&dsdMode!=0)){auto samples=(float*)buffer;float gain=volume.load();for(size_t i=0;i<n/4;++i)samples[i]*=gain;}
        return length;
    }
    static DWORD CALLBACK wasapiProc(void* p,DWORD n,void* user){return ((Engine*)user)->pull(p,n);}
    static DWORD CALLBACK asioProc(BOOL,DWORD,void* p,DWORD n,void* user){return ((Engine*)user)->pull(p,n);}
    static void CALLBACK wasapiNotify(DWORD reason,DWORD dev,void* user){auto e=(Engine*)user;if((int)dev==e->device&&(reason==BASS_WASAPI_NOTIFY_DISABLED||reason==BASS_WASAPI_NOTIFY_FAIL))e->failed=true;}
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
                    if(dsdMode==2){push(pending.data(),pending.size());continue;}
                    carry.insert(carry.end(),pending.begin(),pending.end());auto frames=carry.size()/(channels*2);
                    dop.resize(frames*channels);pack_dop(carry.data(),dop.data(),frames,channels,dopMarker);push((uint8_t*)dop.data(),dop.size()*4);
                    carry.erase(carry.begin(),carry.begin()+frames*channels*2);
                }
            }else{
                std::vector<uint8_t> data(65536-65536%frameSize());
                while(!quit){DWORD n=BASS_ChannelGetData(mixer?mixer:source,data.data(),(DWORD)data.size());if(n==(DWORD)-1){if(BASS_ErrorGetCode()!=BASS_ERROR_ENDED)throw std::runtime_error("音频解码失败");break;}if(!n)break;push(data.data(),n);}
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
            if(!source)throw std::runtime_error("无法解码文件（BASS "+std::to_string(BASS_ErrorGetCode())+"）");
            BASS_CHANNELINFO info{};BASS_ChannelGetInfo(source,&info);sourceRate=rate=info.freq;sourceChannels=channels=info.chans;bits=info.origres;
            duration=BASS_ChannelBytes2Seconds(source,BASS_ChannelGetLength(source,BASS_POS_BYTE));
            if(position>0&&!BASS_ChannelSetPosition(source,BASS_ChannelSeconds2Bytes(source,position),BASS_POS_BYTE))throw std::runtime_error("定位失败");
        }
        if(channels>8||!channels)throw std::runtime_error("最多支持 8 个输出声道");
        if(backend==0){
            BASS_WASAPI_DEVICEINFO info{};if(!BASS_WASAPI_GetDeviceInfo(device,&info))throw std::runtime_error("音频设备已不可用");
            rate=info.mixfreq;if(channels>info.mixchans&&!downmix)throw std::runtime_error("设备声道不足，请确认混成立体声后重试。");
            channels=info.mixchans;
        }else if(!(dsd&&dsdMode!=0)){
            if(forceRate)rate=forceRate;if(downmix)channels=2;
        }
        if(!(dsd&&dsdMode!=0)){
            mixer=BASS_Mixer_StreamCreate(rate,channels,BASS_SAMPLE_FLOAT|BASS_STREAM_DECODE|BASS_MIXER_END);
            if(!mixer||!BASS_Mixer_StreamAddChannel(mixer,source,BASS_MIXER_CHAN_DOWNMIX|BASS_MIXER_CHAN_NORAMPIN))throw std::runtime_error("无法配置 PCM 转换链路");
        }
        if(backend==2){
            if(!BASS_ASIO_Init(device,BASS_ASIO_THREAD|BASS_ASIO_JOINORDER))throw std::runtime_error("ASIO 驱动无法打开："+std::to_string(BASS_ASIO_ErrorGetCode()));
            asio=true;
            BASS_ASIO_INFO info{};BASS_ASIO_GetInfo(&info);
            for(unsigned c=0;c<channels;++c)if(mapping[c]<0||mapping[c]>=(int)info.outputs)throw std::runtime_error("ASIO 输出通道不足或映射无效");
            if(dsd&&dsdMode==2&&!BASS_ASIO_SetDSD(TRUE))throw std::runtime_error("当前 ASIO 驱动不支持原生 DSD");
            if(!BASS_ASIO_CheckRate(rate)||!BASS_ASIO_SetRate(rate))throw std::runtime_error("设备不支持当前采样率，请修改输出格式");
            int first=mapping[0];
            if(!BASS_ASIO_ChannelEnable(FALSE,first,asioProc,this))throw std::runtime_error("ASIO 通道启用失败");
            for(unsigned c=1;c<channels;++c)if(!BASS_ASIO_ChannelJoin(FALSE,mapping[c],first))throw std::runtime_error("ASIO 通道映射失败");
            if(!BASS_ASIO_ChannelSetFormat(FALSE,first,dsd&&dsdMode==2?BASS_ASIO_FORMAT_DSD_MSB:BASS_ASIO_FORMAT_FLOAT))throw std::runtime_error("ASIO 数据格式不受支持");
            BASS_ASIO_SetNotify(asioNotify,this);latency=(double)BASS_ASIO_GetLatency(FALSE)/(dsd&&dsdMode==2?rate/8:rate);
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
            if(dsd&&dsdMode==1&&(info.format==BASS_WASAPI_FORMAT_8BIT||info.format==BASS_WASAPI_FORMAT_16BIT))throw std::runtime_error("DoP 输出至少需要 24 位设备格式");
            latency=(double)info.buflen/(rate*channels*4);BASS_WASAPI_SetNotify(wasapiNotify,this);
        }
        producer=std::thread([this]{produce();});
        auto deadline=GetTickCount64()+15000;
        while(head<rate*frameSize()/(dsd&&dsdMode==2?80:10)&&!ended&&!failed){if(GetTickCount64()>deadline)throw std::runtime_error("音频预缓冲超时");Sleep(2);}
        if(failed)throw std::runtime_error(failure.empty()?"设备初始化失败":failure);
    }
    void resume(){activate();bool ok=asio?BASS_ASIO_Start(0,0):BASS_WASAPI_Start();if(!ok)throw std::runtime_error("音频设备启动失败");playing=true;}
    double position()const{double bytesPerSecond=(double)rate*frameSize()/(dsd&&dsdMode==2?8:1);return std::clamp(offset+played.load()/bytesPerSecond-(playing?latency:0),0.0,duration);}
};
static std::unique_ptr<Engine> engine;

API int luma_init(const wchar_t* directory){std::lock_guard lock(gate);try{appDir=directory;SetDllDirectoryW(directory);BASS_SetConfig(BASS_CONFIG_UNICODE,TRUE);if(!BASS_Init(0,44100,0,nullptr,nullptr)&&BASS_ErrorGetCode()!=BASS_ERROR_ALREADY)throw std::runtime_error("BASS 初始化失败");for(auto name:{L"bassflac.dll",L"bassape.dll",L"bassalac.dll",L"bassopus.dll",L"basswv.dll"}){auto p=BASS_PluginLoad((appDir/name).c_str(),BASS_UNICODE);if(p)plugins.push_back(p);}initialized=true;return 1;}catch(const std::exception& e){errorText=e.what();return 0;}}
API const char* luma_error(){std::lock_guard lock(gate);resultText=errorText;return resultText.c_str();}
API const char* luma_devices(){std::lock_guard lock(gate);std::ostringstream o;o<<"[";bool comma=false;BASS_WASAPI_DEVICEINFO d{};for(int i=0;BASS_WASAPI_GetDeviceInfo(i,&d);++i){if(!(d.flags&BASS_DEVICE_ENABLED)||(d.flags&(BASS_DEVICE_INPUT|BASS_DEVICE_LOOPBACK)))continue;if(comma)o<<",";comma=true;o<<"{\"index\":"<<i<<",\"backend\":1,\"name\":"<<quote(d.name?d.name:"")<<",\"id\":"<<quote(d.id?d.id:"")<<",\"default\":"<<((d.flags&BASS_DEVICE_DEFAULT)?"true":"false")<<",\"channels\":"<<d.mixchans<<",\"rate\":"<<d.mixfreq<<"}";}BASS_ASIO_DEVICEINFO a{};for(int i=0;BASS_ASIO_GetDeviceInfo(i,&a);++i){if(comma)o<<",";comma=true;o<<"{\"index\":"<<i<<",\"backend\":2,\"name\":"<<quote(a.name?a.name:"")<<",\"id\":"<<quote(a.driver?a.driver:"")<<",\"default\":false,\"channels\":0,\"rate\":0}";}o<<"]";resultText=o.str();return resultText.c_str();}
API int luma_open(const wchar_t* file,int track,int backend,int device,int mode,int pcmRate,int forceRate,int downmix,const int* map,float volume,double position){std::lock_guard lock(gate);try{engine.reset();auto next=std::make_unique<Engine>();next->file=file;next->track=track;next->backend=backend;next->device=device;next->dsdMode=mode;next->pcmRate=pcmRate;next->forceRate=forceRate;next->downmix=downmix!=0;next->volume=volume;if(map){for(int i=0;i<8;++i){next->mapping[i]=map[i];for(int j=0;j<i;++j)if(map[i]==map[j])throw std::runtime_error("通道映射不能重复");}}next->open(position);engine=std::move(next);engine->resume();errorText.clear();return 1;}catch(const std::exception& e){errorText=e.what();engine.reset();return 0;}}
API int luma_pause(int pause){std::lock_guard lock(gate);try{if(!engine)return 0;engine->activate();if(pause){if(engine->asio)BASS_ASIO_Stop();else BASS_WASAPI_Stop(FALSE);engine->playing=false;}else engine->resume();return 1;}catch(const std::exception&e){errorText=e.what();return 0;}}
API void luma_stop(){std::lock_guard lock(gate);engine.reset();}
API void luma_volume(float v){std::lock_guard lock(gate);if(engine)engine->volume=std::clamp(v,0.f,1.f);}
API const char* luma_state(){std::lock_guard lock(gate);if(!engine){resultText="{}";return resultText.c_str();}auto&e=*engine;bool done=e.ended&&e.head==e.tail;std::ostringstream o;o<<"{\"playing\":"<<(e.playing?"true":"false")<<",\"ended\":"<<(done?"true":"false")<<",\"failed\":"<<(e.failed?"true":"false")<<",\"error\":"<<quote(e.failure)<<",\"position\":"<<e.position()<<",\"duration\":"<<e.duration<<",\"sourceRate\":"<<e.sourceRate<<",\"sourceChannels\":"<<e.sourceChannels<<",\"rate\":"<<e.rate<<",\"channels\":"<<e.channels<<",\"bits\":"<<e.bits<<",\"dsd\":"<<(e.dsd?"true":"false")<<",\"mode\":"<<e.dsdMode<<",\"backend\":"<<e.backend<<",\"underruns\":"<<e.underruns<<"}";resultText=o.str();return resultText.c_str();}
API void luma_shutdown(){std::lock_guard lock(gate);engine.reset();for(auto p:plugins)BASS_PluginFree(p);plugins.clear();BASS_Free();initialized=false;}
