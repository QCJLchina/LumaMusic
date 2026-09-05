#pragma once
#include <windows.h>
#include <string>
#include <sstream>
#include <iomanip>
#include <cstdint>
inline std::string utf8(const std::wstring& w) {
    int n=WideCharToMultiByte(CP_UTF8,0,w.data(),(int)w.size(),nullptr,0,nullptr,nullptr);
    std::string s(n,0); WideCharToMultiByte(CP_UTF8,0,w.data(),(int)w.size(),s.data(),n,nullptr,nullptr);return s;
}
inline std::wstring wide(const std::string& s) {
    int n=MultiByteToWideChar(CP_UTF8,0,s.data(),(int)s.size(),nullptr,0);
    std::wstring w(n,0);MultiByteToWideChar(CP_UTF8,0,s.data(),(int)s.size(),w.data(),n);return w;
}
inline std::string quote(const std::string& s) {
    std::ostringstream o; o << '"';
    for(unsigned char c:s) {switch(c) {case '"':o<<"\\\"";break;case '\\':o<<"\\\\";break;case '\n':o<<"\\n";break;case '\r':break;case '\t':o<<"\\t";break;default:if(c<32)o<<"\\u"<<std::hex<<std::setw(4)<<std::setfill('0')<<(int)c;else o<<c;}}
    o << '"'; return o.str();
}
inline std::string ansi(const char* text) {
    if(!text)return {};
    int n=MultiByteToWideChar(CP_ACP,0,text,-1,nullptr,0);std::wstring w(n,0);
    MultiByteToWideChar(CP_ACP,0,text,-1,w.data(),n);if(!w.empty())w.pop_back();return utf8(w);
}
#pragma pack(push,1)
struct DsdHeader { uint32_t magic=0x414d554c, version=1, rate=0, channels=0, sourceRate=0, format=0, speakerConfig=0; double duration=0; };
#pragma pack(pop)
