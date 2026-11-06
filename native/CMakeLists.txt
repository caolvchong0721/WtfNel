cmake_minimum_required(VERSION 3.21)
project(WtfNelPayloads LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

if(MSVC)
    set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
    add_compile_options(/utf-8)
endif()

if(NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
    message(FATAL_ERROR "需要 64 位工具链：目标 JVM 和游戏都是 x64")
endif()

set(PROXY_DIR "${CMAKE_CURRENT_SOURCE_DIR}/proxy")

add_library(proxy SHARED
    ${PROXY_DIR}/ReflectiveLoader.c
    ${PROXY_DIR}/loader.cpp
    ${PROXY_DIR}/env.cpp
    ${PROXY_DIR}/random_name.cpp
    ${PROXY_DIR}/classfile.cpp
    ${PROXY_DIR}/class_edit.cpp
    ${PROXY_DIR}/trampolines.cpp
    ${PROXY_DIR}/relay_handler.cpp
    ${PROXY_DIR}/connection_hook.cpp
    ${PROXY_DIR}/world_cache.cpp
    ${PROXY_DIR}/b_server.cpp
)

target_include_directories(proxy PRIVATE
    ${PROXY_DIR}
    ${PROXY_DIR}/include
)

target_compile_definitions(proxy PRIVATE
    REFLECTIVEDLLINJECTION_CUSTOM_DLLMAIN
    WIN_X64
    WIN32_LEAN_AND_MEAN
    NOMINMAX
    _CRT_SECURE_NO_WARNINGS
)

target_link_libraries(proxy PRIVATE
    psapi
    ws2_32
    kernel32
    user32
    advapi32
)

set_target_properties(proxy PROPERTIES
    OUTPUT_NAME "WtfNel.Payload.Proxy"
    PREFIX ""
)

set(HWID_DIR "${CMAKE_CURRENT_SOURCE_DIR}/hwid")

add_library(hwid SHARED
    ${HWID_DIR}/dllmain.cpp
    ${HWID_DIR}/minhook/src/buffer.c
    ${HWID_DIR}/minhook/src/hook.c
    ${HWID_DIR}/minhook/src/trampoline.c
    ${HWID_DIR}/minhook/src/hde/hde32.c
    ${HWID_DIR}/minhook/src/hde/hde64.c
    ${PROXY_DIR}/ReflectiveLoader.c
)

target_include_directories(hwid PRIVATE
    ${HWID_DIR}
    ${HWID_DIR}/minhook
    ${HWID_DIR}/minhook/include
    ${PROXY_DIR}/include
)

target_compile_definitions(hwid PRIVATE
    REFLECTIVEDLLINJECTION_CUSTOM_DLLMAIN
    WIN_X64
    WIN32_LEAN_AND_MEAN
    NOMINMAX
    _CRT_SECURE_NO_WARNINGS
)

target_link_libraries(hwid PRIVATE
    psapi
    ws2_32
    kernel32
    user32
    advapi32
    netapi32
    iphlpapi
    setupapi
    wbemuuid
    wininet
)

set_target_properties(hwid PROPERTIES
    OUTPUT_NAME "WtfNel.Payload.HwidSpoof"
    PREFIX ""
    LINK_FLAGS "/IGNORE:4099"
)
