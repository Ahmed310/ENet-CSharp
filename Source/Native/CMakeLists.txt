cmake_minimum_required(VERSION 3.10)
project(enet C)

set(ENET_DEBUG "0" CACHE BOOL "Enable debug functionality")
set(ENET_NO_POOL "0" CACHE BOOL "Compile the packet buffer pool out (plain malloc/free per packet)")

if (WIN32)
    set(ENET_EXCLUDE_WINDOWS_H "0" CACHE BOOL "Exclude \"windows.h\" header")
endif()

set(ENET_STATIC "0" CACHE BOOL "Create a static library")
set(ENET_SHARED "0" CACHE BOOL "Create a shared library")

if (MSYS OR MINGW)
    set(CMAKE_C_FLAGS "-static")

    add_definitions(-DWINVER=0x0601)
    add_definitions(-D_WIN32_WINNT=0x0601)
endif()

if (ENET_DEBUG)
    add_definitions(-DENET_DEBUG)
endif()

if (ENET_NO_POOL)
    add_definitions(-DENET_NO_POOL)
endif()

# The buffer pool's per-thread caches use pthread keys on POSIX
if (UNIX)
    set(THREADS_PREFER_PTHREAD_FLAG ON)
    find_package(Threads REQUIRED)
endif()

if (ENET_STATIC)
    add_library(enet_static STATIC enet.c ${SOURCES})

    if (NOT UNIX)
        target_link_libraries(enet_static winmm ws2_32)
        SET_TARGET_PROPERTIES(enet_static PROPERTIES PREFIX "")
    else()
        target_link_libraries(enet_static Threads::Threads)
    endif()
endif()

if (ENET_SHARED)
    if (APPLE)
        SET(CMAKE_OSX_ARCHITECTURES "arm64;x86_64")
    endif()
    add_library(enet SHARED enet.c ${SOURCES})
    # Only the DLL exports symbols and carries the pool's DllMain thread-exit hook; a static
    # library configured alongside it must not
    target_compile_definitions(enet PRIVATE ENET_DLL)

    if (NOT UNIX)
        target_link_libraries(enet winmm ws2_32)
        SET_TARGET_PROPERTIES(enet PROPERTIES PREFIX "")
    else()
        target_link_libraries(enet Threads::Threads)
    endif()
endif()
