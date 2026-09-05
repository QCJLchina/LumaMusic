#pragma once
#include <cstdint>
#include <cstring>
#include <algorithm>
// Interleaved MSB-first DSD bytes -> exactly representable 24-bit DoP samples.
inline void pack_dop(const uint8_t* dsd, float* out, size_t frames, unsigned channels, bool& alternate) {
    for(size_t f=0;f<frames;++f) {
        uint32_t marker=alternate?0xfa:0x05;alternate=!alternate;
        for(unsigned c=0;c<channels;++c) {
            uint32_t value=(marker<<16)|(uint32_t(dsd[(f*2+1)*channels+c])<<8)|dsd[f*2*channels+c];
            int32_t signedValue=(value&0x800000)?int32_t(value|0xff000000):int32_t(value);
            out[f*channels+c]=float(signedValue)/8388608.f;
        }
    }
}
inline void dop_silence(float* out,size_t frames,unsigned channels,bool& alternate) {
    for(size_t f=0;f<frames;++f) {
        uint32_t value=((alternate?0xfa:0x05)<<16)|0x6969;alternate=!alternate;
        int32_t v=(value&0x800000)?int32_t(value|0xff000000):int32_t(value);
        for(unsigned c=0;c<channels;++c)out[f*channels+c]=float(v)/8388608.f;
    }
}
