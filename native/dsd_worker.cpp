#include "common.h"
#include "sacd/sacd_disc.h"
#include "sacd/sacd_dsdiff.h"
#include "sacd/sacd_dsf.h"
#include "DSDPCMConverterEngine.h"
#include "decoder.h"
#include <filesystem>
#include <memory>
#include <vector>
#include <iostream>
#include <io.h>
#include <fcntl.h>
#include <cmath>
#include <algorithm>

void log_printf(const char*, ...) {}
static void writeBytes(const void* data,size_t count) { if(fwrite(data,1,count,stdout)!=count) throw std::runtime_error("Output pipe closed"); }
static void block(const void* data,uint32_t count){ writeBytes(&count,4); if(count)writeBytes(data,count); fflush(stdout); }

int wmain(int argc,wchar_t** argv) {
    try {
        if(argc<3) throw std::runtime_error("Usage: LumaDsd --probe file | --stream file track raw|pcm rate seconds");
        _setmode(_fileno(stdout),_O_BINARY);
        std::filesystem::path path(argv[2]); auto ext=path.extension().wstring();
        std::transform(ext.begin(),ext.end(),ext.begin(),towlower);
        sacd_media_file_t media;
        if(!media.open(utf8(path.wstring()),false)) throw std::runtime_error("Cannot open DSD file");
        std::unique_ptr<sacd_reader_t> reader;
        if(ext==L".iso") reader=std::make_unique<sacd_disc_t>();
        else if(ext==L".dff") reader=std::make_unique<sacd_dsdiff_t>();
        else reader=std::make_unique<sacd_dsf_t>();
        reader->set_mode(ACCESS_MODE_TWOCH|ACCESS_MODE_MULCH);
        if(!reader->open(&media)) throw std::runtime_error("Invalid or unsupported SACD/DSD file");
        if(std::wstring(argv[1])==L"--probe") {
            std::ostringstream json;json<<"[";
            auto count=reader->get_track_count();
            if(count>2048) throw std::runtime_error("Invalid track count");
            for(uint32_t i=0;i<count;++i) {
                auto track=reader->get_track_number(i);
                if(!reader->select_track(track)) continue;
                kodi::addon::AudioDecoderInfoTag tag;reader->get_info(track,tag);
                if(i)json<<",";
                json<<"{\"subsong\":"<<track<<",\"title\":"<<quote(tag.title)<<",\"artist\":"<<quote(tag.artist)
                    <<",\"album\":"<<quote(tag.album)<<",\"duration\":"<<reader->get_duration()<<",\"channels\":"<<reader->get_channels()
                    <<",\"sampleRate\":"<<reader->get_samplerate()<<",\"dst\":"<<(reader->is_dst()?"true":"false")<<"}";
            }
            json<<"]";auto s=json.str();writeBytes(s.data(),s.size());return 0;
        }
        if(argc<7)throw std::runtime_error("Missing stream parameters");
        int track=_wtoi(argv[3]), rate=_wtoi(argv[5]);double seconds=_wtof(argv[6]);
        bool pcm=std::wstring(argv[4])==L"pcm";
        if(!reader->select_track(track))throw std::runtime_error("Cannot select track");
        int channels=reader->get_channels(),sourceRate=reader->get_samplerate(),fps=reader->get_framerate();
        if(channels<1||channels>8||sourceRate<2822400||sourceRate>49152000||fps<1||fps>1000)throw std::runtime_error("Unsupported DSD format");
        double duration=reader->get_duration();
        if(!std::isfinite(duration)||duration<=0)throw std::runtime_error("Invalid duration");
        seconds=std::clamp(seconds,0.0,duration);
        // Seek to a frame boundary, then trim the decoded frame to the requested sample.
        double boundary=std::floor(seconds*fps)/fps;
        if(boundary>0&&!reader->seek(boundary))throw std::runtime_error("Cannot seek DSD track");
        int frameBytes=sourceRate/8/fps*channels;
        std::vector<uint8_t> input(frameBytes*2),raw(frameBytes);
        std::vector<float> samples;
        std::unique_ptr<DSDPCMConverterEngine> converter;
        if(pcm) {
            if(rate<44100||rate>768000||rate%fps)throw std::runtime_error("Invalid PCM conversion rate");
            converter=std::make_unique<DSDPCMConverterEngine>();converter->set_gain(0);
            if(converter->init(channels,fps,sourceRate,rate,conv_type_e::DIRECT,true,nullptr,0)<0)throw std::runtime_error("DSD PCM converter initialization failed");
            samples.resize((rate/fps+1024)*channels);
        }
        DsdHeader header;header.rate=pcm?rate:sourceRate;header.channels=channels;header.sourceRate=sourceRate;
        header.format=pcm?0:1;header.speakerConfig=reader->get_loudspeaker_config();header.duration=duration;
        writeBytes(&header,sizeof(header));fflush(stdout);
        std::unique_ptr<dst::decoder_t> decoder;
        int64_t trim=(int64_t)std::llround((seconds-boundary)*(pcm?rate:sourceRate/8));
        int64_t remaining=(int64_t)std::llround((duration-seconds)*(pcm?rate:sourceRate/8));
        while(remaining>0) {
            size_t size=frameBytes;frame_type_e kind=frame_type_e::INVALID;
            if(!reader->read_frame(input.data(),&size,&kind))break;
            if(size>(size_t)frameBytes)throw std::runtime_error("Invalid DSD frame length");
            uint8_t* data=input.data();
            if(kind==frame_type_e::DST) {
                if(!decoder) {decoder=std::make_unique<dst::decoder_t>();if(decoder->init(channels,sourceRate/8/fps)!=0)throw std::runtime_error("DST initialization failed");}
                if(decoder->decode(input.data(),(unsigned)size*8,raw.data())!=0)throw std::runtime_error("Corrupt DST frame");
                data=raw.data();size=raw.size();
            } else if(kind!=frame_type_e::DSD) throw std::runtime_error("Invalid DSD frame");
            int64_t frames=pcm?converter->convert(data,(int)size,samples.data())/channels:(int64_t)size/channels;
            if(frames<0)throw std::runtime_error("Conversion failed");
            auto skip=std::min(trim,frames);trim-=skip;frames-=skip;frames=std::min(frames,remaining);
            if(frames>0){if(pcm)block(samples.data()+skip*channels,(uint32_t)(frames*channels*4));else block(data+skip*channels,(uint32_t)(frames*channels));remaining-=frames;}
        }
        block(nullptr,0);return 0;
    }catch(const std::exception& e){fprintf(stderr,"%s\n",e.what());return 1;}
}
