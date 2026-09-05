cmake_minimum_required(VERSION 3.20)
project(LumaNative LANGUAGES C CXX)
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
add_compile_definitions(NOMINMAX WIN32_LEAN_AND_MEAN _CRT_SECURE_NO_WARNINGS)
add_compile_options(/utf-8)
set(CMAKE_RUNTIME_OUTPUT_DIRECTORY "${CMAKE_SOURCE_DIR}/build/native-bin")
set(CMAKE_RUNTIME_OUTPUT_DIRECTORY_RELEASE "${CMAKE_SOURCE_DIR}/build/native-bin")
set(SACD "${CMAKE_SOURCE_DIR}/build/sacd")
file(GLOB ID3_C "${SACD}/lib/id3v2lib/src/*.c")
add_library(id3 STATIC ${ID3_C})
target_include_directories(id3 PRIVATE "${SACD}/lib/id3v2lib/include" "${SACD}/lib/id3v2lib/include/id3v2lib")
add_executable(LumaDsd native/dsd_worker.cpp
    "${SACD}/src/sacd/sacd_disc.cpp" "${SACD}/src/sacd/sacd_dsdiff.cpp"
    "${SACD}/src/sacd/sacd_dsf.cpp" "${SACD}/src/sacd/sacd_dsd.cpp"
    "${SACD}/src/sacd/sacd_media.cpp" "${SACD}/src/sacd/scarletbook.cpp"
    "${SACD}/src/sacd/id3_tagger.cpp" "${SACD}/lib/libdstdec/decoder/decoder.cpp"
    "${SACD}/lib/libdsdpcm/DSDPCMConverterEngine.cpp" "${SACD}/lib/libdsdpcm/Fir_IPP.cpp")
target_include_directories(LumaDsd PRIVATE native native/compat "${SACD}/src"
    "${SACD}/lib/libdstdec" "${SACD}/lib/libdstdec/decoder" "${SACD}/lib/libdsdpcm")
target_link_libraries(LumaDsd PRIVATE id3)
add_library(LumaAudio SHARED native/engine.cpp)
target_include_directories(LumaAudio PRIVATE native vendor/bass/c vendor/bassasio/c vendor/basswasapi/c vendor/bassmix/c)
target_link_libraries(LumaAudio PRIVATE
    "${CMAKE_SOURCE_DIR}/vendor/bass/c/x64/bass.lib"
    "${CMAKE_SOURCE_DIR}/vendor/bassasio/c/x64/bassasio.lib"
    "${CMAKE_SOURCE_DIR}/vendor/basswasapi/c/x64/basswasapi.lib"
    "${CMAKE_SOURCE_DIR}/vendor/bassmix/c/x64/bassmix.lib")
add_executable(LumaNativeTests tests/native_tests.cpp)
target_include_directories(LumaNativeTests PRIVATE native)
enable_testing()
add_test(NAME AudioPayloads COMMAND LumaNativeTests)
