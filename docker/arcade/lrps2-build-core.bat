@echo off
rem Build pcsx2_libretro.dll (LRPS2) from source — CMake + Ninja + MSVC, mirroring
rem docker/arcade/dolphin-build-core.bat (the proven custom-core recipe on this box).
rem
rem WHY CMAKE, NOT THE LIBRETRO MAKEFILE (2026-07-15): the Makefile's windows_msvc2017_* branch is
rem vestigial — it lacks the Windows platform defines (-D__SSE4_1__, -D_WIN32_WINNT, HAVE_D3D11/12,
rem ZIP_STATIC...) that only its MinGW branch carries, and that branch's own comment says it "targets
rem full feature parity with the cmake LIBRETRO=ON Windows build" — i.e. CMake IS the canonical
rem Windows build. (The Makefile route died at common/VectorIntrin.h "requires at least SSE2".)
rem The stock nightly DLL imports no CRT DLL at all -> static /MT MSVC; match it.
rem
rem ⚠ Dolphin lesson (see dolphin-build-core.bat): verify /O2 + /DNDEBUG survive on every C++ TU
rem after any CMAKE_CXX_FLAGS_RELEASE override in the tree — a plain set() clobber ships a
rem no-optimization "Release" core that loses 50% of its speed invisibly.
set "PATH=%PATH%;C:\Program Files (x86)\Microsoft Visual Studio\Installer"
call "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\Tools\VsDevCmd.bat" -arch=x64 -host_arch=x64 -no_logo
if errorlevel 1 exit /b 1
where cl || exit /b 1
set "CMAKE=C:\Users\Atoramos\AppData\Roaming\Python\Python313\site-packages\cmake\data\bin\cmake.exe"
set "PATH=%PATH%;C:\Users\Atoramos\AppData\Roaming\Python\Python313\Scripts"
cd /d D:\Arcade\build\lrps2
rem CMAKE_POLICY_VERSION_MINIMUM: the vendored 3rdparty (cpuinfo/clog) declares cmake_minimum_required
rem below 3.5, which CMake 4.x refuses outright; this flag is the documented escape hatch.
"%CMAKE%" -S . -B bld -G Ninja -DLIBRETRO=ON -DCMAKE_BUILD_TYPE=Release -DCMAKE_C_COMPILER=cl -DCMAKE_CXX_COMPILER=cl -DCMAKE_POLICY_VERSION_MINIMUM=3.5
if errorlevel 1 exit /b 1
"%CMAKE%" --build bld --target pcsx2_libretro --parallel 14
