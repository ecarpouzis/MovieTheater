#!/usr/bin/env bash
# DIAGNOSTIC-ONLY build of melonDS DS (JesseTG/melonDS-ds v1.2.0). Production ships the STOCK
# melondsds_libretro.dll — this from-source build (RelWithDebInfo, DWARF symbols) exists only to
# gdb the OpenGL renderer, which is how the NDS-opengl crash was root-caused (GLCompositor::
# RenderFrame:242 → the fix was usesLibCo + the worker core-options patch, NOT a core change).
# Keep it for the next time a melonDS-internal crash needs a symbolized backtrace.
# GCC/UCRT64, NOT MSVC: the melonDS core uses GCC-isms (__attribute__((always_inline)), GNU min/max)
# that MSVC rejects — its native toolchain is GCC, same as the libretro buildbot. UCRT64 also matches
# the CloudRetro worker's own toolchain, so the DLL loads cleanly. RelWithDebInfo => symbols + a
# .debug for gdb'ing the OpenGL renderer first-frame crash.
# Usage: bash melonds-build-core.sh [buildonly]
set -e
export PATH="/d/msys64/ucrt64/bin:$PATH"
export CC=gcc CXX=g++
cd /d/Arcade/build/melonds-ds
if [ "$1" != "buildonly" ]; then
  cmake -S . -B bld-gcc -G Ninja \
    -DCMAKE_BUILD_TYPE=RelWithDebInfo \
    -DCMAKE_C_COMPILER=gcc -DCMAKE_CXX_COMPILER=g++ \
    -DCMAKE_POLICY_VERSION_MINIMUM=3.5 \
    -DBUILD_TESTING=OFF
fi
cmake --build bld-gcc --target melondsds_libretro --parallel 14
