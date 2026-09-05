#pragma once
// SACD text encodings, backed by Windows NLS. No external iconv runtime required.
#include <windows.h>
#include <string>
#include <cerrno>
using iconv_t = intptr_t;
inline iconv_t iconv_open(const char*, const char* from) {
    std::string name(from);
    if(name=="SHIFT_JIS") return 932;
    if(name=="KSC5636") return 949;
    if(name=="GB2312") return 936;
    if(name=="BIG5") return 950;
    if(name=="ISO8859-1") return 28591;
    return 20127;
}
inline int iconv_close(iconv_t){return 0;}
inline size_t iconv(iconv_t cp, const char** input, size_t* inleft, char** output, size_t* outleft) {
    if(!input || !*input) return 0;
    int n=MultiByteToWideChar((UINT)cp,0,*input,(int)*inleft,nullptr,0);
    if(n<=0){errno=EILSEQ;return (size_t)-1;}
    std::wstring w(n,0); MultiByteToWideChar((UINT)cp,0,*input,(int)*inleft,w.data(),n);
    int bytes=WideCharToMultiByte(CP_UTF8,0,w.data(),n,nullptr,0,nullptr,nullptr);
    if((size_t)bytes>*outleft){errno=E2BIG;return (size_t)-1;}
    WideCharToMultiByte(CP_UTF8,0,w.data(),n,*output,bytes,nullptr,nullptr);
    *input+=*inleft; *inleft=0; *output+=bytes; *outleft-=bytes; return 0;
}
