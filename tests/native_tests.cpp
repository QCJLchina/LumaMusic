#include "audio_math.h"
#include <vector>
#include <cmath>
#include <cstdio>
#include <stdexcept>
static void check(bool ok){if(!ok)throw std::runtime_error("Audio payload regression");}
int main(){
 try {
    for(unsigned channels:{1,2,6,8}){
        std::vector<uint8_t> input(1024*channels*2);for(size_t i=0;i<input.size();++i)input[i]=(uint8_t)((i*73+19)%256);
        std::vector<float> output(1024*channels);bool marker=false;pack_dop(input.data(),output.data(),513,channels,marker);
        pack_dop(input.data()+513*channels*2,output.data()+513*channels,511,channels,marker);
        for(size_t f=0;f<1024;++f)for(unsigned c=0;c<channels;++c){int32_t v=(int32_t)std::lrint(output[f*channels+c]*8388608.f);check(((v>>16)&255)==(f%2?0xfa:0x05));check((v&255)==input[f*channels*2+c]);check(((v>>8)&255)==input[(f*2+1)*channels+c]);}
        check(!marker);
    }
    bool marker=true;float silence[12];dop_silence(silence,6,2,marker);for(int i=0;i<12;++i)check(((int)std::lrint(silence[i]*8388608.f)&65535)==0x6969);
    puts("PASS: DoP roundtrip, signed 24-bit, split blocks, 1/2/6/8 channels, DSD silence");return 0;
 }catch(const std::exception&e){puts(e.what());return 1;}
}
