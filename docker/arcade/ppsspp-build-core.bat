@echo off
rem Build our custom PPSSPP libretro core using the LIBRETRO BUILDBOT'S OWN RECIPE:
rem   libretro/Makefile, platform=windows_msvc2019_desktop_x64  (MSVC, static /MT CRT)
rem
rem WHY NOT CMake, and WHY NOT MinGW -- both were tried, measured, and rejected:
rem
rem  * CMake+MSVC builds and runs, but is measurably SLOWER than the stock nightly with IDENTICAL core
rem    options. God of War: Chains of Olympus, same driven 300 s, maxTick per 5 s window:
rem        stock             + IR JIT : median 11.0  p90 13.7  max 21.4
rem        ours (CMake/MSVC) + IR JIT : median 13.2  p90 30.8  max 44.7   <- pure BUILD penalty
rem    Stock imports no VCRUNTIME140 (static /MT CRT); our CMake build was /MD. Different build, and
rem    whatever else CMake's libretro target does, it is not what ships.
rem
rem  * MinGW/GCC CANNOT build this core at all. PPSSPP's checked-in Windows ffmpeg
rem    (ffmpeg/Windows/x86_64/lib) is produced by ffmpeg/windows_x64-build.sh with --toolchain=msvc --
rem    they are MSVC STATIC libs, and GNU ld dies on them with "undefined reference to __isa_available"
rem    (an MSVC CRT internal). The stock DLL also carries no "GCC: (GNU)" stamp. Stock is MSVC. Proven
rem    the hard way with a full standalone msvcrt GCC toolchain: it compiled every PPSSPP TU and then
rem    died on ffmpeg. Do not retry MinGW.
rem
rem So: same Makefile, same compiler, same flags as the buildbot -- only our patch differs.
rem
rem ⚠ THE MAKEFILE BUILDS ITS OWN INCLUDE/LIB. It does NOT use VsDevCmd's environment: it locates VS via
rem `bash VSWhere.sh`, queries the registry for the Windows SDK, and then `export`s INCLUDE/LIB itself
rem (Makefile ~345-392). On this box VSWhere.sh returns EMPTY (its `cmd //c` shim fails under MSYS), so
rem INCLUDE comes out garbage and every file dies with
rem     fatal error C1083: Cannot open include file: 'vcruntime.h'
rem That error looks like a broken toolchain and is not -- it is a broken DETECTION. Passing
rem VSInstallPath on the command line overrides it (make command-line vars beat makefile `:=`), and the
rem Makefile then derives VcCompilerToolsVer / INCLUDE / LIB / PATH correctly by itself. It also sets
rem CC=CXX=cl.exe on its own, so do not pass those.
rem
rem MSYS2's tools ARE required here (cygpath, bash, reg, cat, grep) -- this Makefile is written for them.
setlocal
set "MSYS=D:\msys64\usr\bin"
set "VSROOT=/c/Program Files/Microsoft Visual Studio/18/Community"

if not exist "%MSYS%\make.exe" (echo FATAL: MSYS2 make not found at %MSYS% & exit /b 1)
set "PATH=%PATH%;%MSYS%"

cd /d D:\Arcade\build\ppsspp\libretro

rem VSInstallPath MUST be passed as ONE quoted argument -- the path contains spaces ("Program Files"),
rem and unquoted it splits, giving the tell-tale  cat: /c/Program/VC/Auxiliary/...: No such file.
rem (Also note: `bash` on PATH is WSL's bash, not MSYS2's, which is why the Makefile's own VSWhere.sh
rem detection fails on this box. Overriding VSInstallPath makes that path irrelevant -- the failing
rem $(shell ...) still runs but its result is discarded, so the noise in the log is harmless.)
make platform=windows_msvc2019_desktop_x64 "VSInstallPath=%VSROOT%" clean
make platform=windows_msvc2019_desktop_x64 "VSInstallPath=%VSROOT%" -j14
if errorlevel 1 (echo BUILD FAILED & exit /b 1)
echo BUILD OK
