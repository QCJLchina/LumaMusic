#pragma once
#include "AddonBase.h"
#include <windows.h>
#include <string>
#include <vector>
#include <cstdio>
#include <cstdint>
#include <cstring>
#include <memory>
namespace kodi::vfs {
inline std::wstring wide(const std::string& s) {
    int n = MultiByteToWideChar(CP_UTF8, 0, s.data(), (int)s.size(), nullptr, 0);
    std::wstring w(n, 0); MultiByteToWideChar(CP_UTF8, 0, s.data(), (int)s.size(), w.data(), n); return w;
}
struct FileStatus {};
inline bool StatFile(const std::string&, FileStatus&) { return false; }
class CFile {
    FILE* file = nullptr;
public:
    ~CFile() { if(file) fclose(file); }
    bool OpenFile(const std::string& path) { file = _wfopen(wide(path).c_str(), L"rb"); return file != nullptr; }
    bool OpenFileForWrite(const std::string&) { return false; } // decoder is deliberately read-only
    int64_t Seek(int64_t offset, int mode = SEEK_SET) { return file && !_fseeki64(file, offset, mode) ? _ftelli64(file) : -1; }
    size_t Read(void* data, size_t count) { return file ? fread(data, 1, count, file) : 0; }
    size_t Write(const void*, size_t) { return 0; }
    bool IoControlGetSeekPossible() { return file != nullptr; }
    int64_t GetPosition() { return file ? _ftelli64(file) : -1; }
    int64_t GetLength() { auto pos = GetPosition(); auto length = Seek(0, SEEK_END); Seek(pos); return length; }
    void Truncate(int64_t) {}
};
}
