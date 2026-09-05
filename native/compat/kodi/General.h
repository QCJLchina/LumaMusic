#pragma once
#include <cstdio>
#include <cstdarg>
#define ADDON_LOG_ERROR 3
#define ADDON_LOG_FATAL 4
using ADDON_LOG = int;
#define ADDON_LOG_DEBUG 0
#define ADDON_LOG_INFO 1
#define ADDON_LOG_WARNING 2
namespace kodi {
inline void Log(int, const char* format, ...) {
    va_list args; va_start(args, format); vfprintf(stderr, format, args); va_end(args); fputc('\n', stderr);
}
namespace addon { class CSettingValue {}; }
}
