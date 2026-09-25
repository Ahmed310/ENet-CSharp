cmake_minimum_required(VERSION 3.10)
project(enet_pool_bench C)

# Standalone: the benchmark loads the ENet library under test at run time, so one binary can
# measure every build. Configure with: cmake -S Source/Native/bench -B build-bench
add_executable(pool_bench pool_bench.c)

if (UNIX)
    set(THREADS_PREFER_PTHREAD_FLAG ON)
    find_package(Threads REQUIRED)
    target_link_libraries(pool_bench Threads::Threads ${CMAKE_DL_LIBS})
endif()
