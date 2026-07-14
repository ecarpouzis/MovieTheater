@echo off
set "PATH=%PATH%;C:\Program Files (x86)\Microsoft Visual Studio\Installer"
call "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\Tools\VsDevCmd.bat" -arch=x64 -host_arch=x64 -no_logo
if errorlevel 1 exit /b 1
where cl || exit /b 1
set "CMAKE=C:\Users\Atoramos\AppData\Roaming\Python\Python313\site-packages\cmake\data\bin\cmake.exe"
set "PATH=%PATH%;C:\Users\Atoramos\AppData\Roaming\Python\Python313\Scripts"
rem CMAKE_CXX_FLAGS_RELEASE MUST be passed explicitly, and this is not a tuning choice.
rem
rem Dolphin's CMakeLists.txt does, for MSVC:
rem     set(CMAKE_CXX_FLAGS_RELEASE "/MT")
rem which OVERWRITES CMake's default Release flags ("/MD /O2 /Ob2 /DNDEBUG") instead of appending to
rem them. The result is a "Release" build with NO /O2 (MSVC then defaults to /Od) and NO NDEBUG
rem (assertions live) for every C++ file -- i.e. the whole emulator: the PowerPC JIT, VideoCommon,
rem Common. It only clobbers the CXX flags, so the C code still gets /O2, which is why this looks like
rem a working Release build and produces a plausible DLL.
rem
rem Measured cost of not passing this: F-Zero GX races at 30-50 ticks/s with one core pegged and the
rem GPU idle, while the stock nightly (built by the libretro buildbot, which never hits this clobber)
rem runs the same races at 58-60. It cost days of hunting, because every symptom points at the GPU/
rem shader stack and none of it points here. The DLL is also ~30% larger, which is the visible tell.
rem NOTE: passing -DCMAKE_CXX_FLAGS_RELEASE here does NOT work -- upstream's plain set() shadows the
rem cache entry and the flag is silently ignored. The fix lives in CMakeLists.txt (see the MT PATCH
rem note there). Verify after any core update: every C++ TU must show /O2 and /DNDEBUG in build.ninja.
"%CMAKE%" -S . -B bld2 -G Ninja -DLIBRETRO=ON -DCMAKE_BUILD_TYPE=Release -DCMAKE_C_COMPILER=cl -DCMAKE_CXX_COMPILER=cl
if errorlevel 1 exit /b 1
"%CMAKE%" --build bld2 --target dolphin_libretro --parallel 14
