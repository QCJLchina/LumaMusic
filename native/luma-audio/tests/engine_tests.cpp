#include <windows.h>
#include <cstdio>

// native/luma-audio/tests/engine_tests.cpp
// Device-free regressions for the engine's exported pure helpers.

extern "C" __declspec(dllimport) int luma_format_bits(int api, DWORD format);

static int failures = 0;

static void expectBits(const char* name, int actual, int expected) {
    if (actual != expected) {
        ++failures;
        printf("%s: expected %d, got %d\n", name, expected, actual);
    }
}

int main() {
    expectBits("wasapi float", luma_format_bits(0, 0), 32);
    expectBits("wasapi 8bit", luma_format_bits(0, 1), 8);
    expectBits("wasapi 16bit", luma_format_bits(0, 2), 16);
    expectBits("wasapi 24bit", luma_format_bits(0, 3), 24);
    expectBits("wasapi 32bit", luma_format_bits(0, 4), 32);
    expectBits("wasapi unknown", luma_format_bits(0, 99), 0);
    expectBits("asio 16bit", luma_format_bits(1, 16), 16);
    expectBits("asio 24bit", luma_format_bits(1, 17), 24);
    expectBits("asio 32bit", luma_format_bits(1, 18), 32);
    expectBits("asio float", luma_format_bits(1, 19), 32);
    expectBits("asio 32bit24", luma_format_bits(1, 27), 32);
    expectBits("asio dither", luma_format_bits(1, 16 | 0x100), 16);
    expectBits("asio dsd", luma_format_bits(1, 32), 0);
    return failures;
}
