@echo off
rem Build the MovieTheater custom flycast libretro core (flycast_custom_libretro.dll).
rem
rem TOOLCHAIN: MSYS2 MinGW (ucrt64), NOT MSVC. flycast's own libretro build is MinGW -- its CI
rem (.github/workflows/c-cpp.yml) builds the Windows core as x86_64-w64-mingw32 via msys2, and the stock
rem nightly DLL is MXE-mingw (gcc 11.3.0; strings show mingw-w64/MXE paths). An MSVC (VsDevCmd/cl) attempt
rem fails at configure: FindZLIB and FindPkgConfig come up empty because those are MSYS2-provided. ucrt64
rem carries cmake, ninja, pkg-config, and zlib (libz.a + zlib.h). This is the OPPOSITE of the dolphin/ppsspp
rem custom cores (which are MSVC) -- do not copy their recipe here.
rem
rem WHAT THE CUSTOM CORE IS: stock flycast_libretro is a genuine libretro-Vulkan v1 core (vendored
rem libretro_vulkan.h VERSION 1; libretro.cpp registers only the 5 v1 negotiation fields). Its v1
rem create_device (vk_context_lr.cpp VkCreateDevice) receives the frontend's required_device_extensions but
rem IGNORES them, so our M2 zero-copy import (needs VK_KHR_external_memory_win32 /
rem VK_KHR_external_semaphore_win32 on the core's VkDevice) could not attach and dc-Vulkan fell to black/soft.
rem The patch (docker/arcade/flycast-custom-core.patch) makes VkCreateDevice honor required_device_extensions
rem (support-guarded, de-duplicated) and logs the OIT-critical device features (fragmentStoresAndAtomics etc.)
rem so the probe proves per-pixel OIT is safe. flycast already enables its OWN queried features (all
rem supported), so the frontend's zeroed required_features cannot break OIT.
rem
rem /O2: flycast's CMakeLists does NOT clobber CMAKE_CXX_FLAGS_RELEASE (only DEBUG flags) -- Release keeps
rem full optimization. Verify DLL size ~= stock nightly after any update (the dolphin F-Zero de-opt lesson).
rem
rem SOURCE: D:\Arcade\build\flycast (flyinghead/flycast) patched with flycast-custom-core.patch. The custom
rem NAME pins it vs libretro.cores.repo.sync nightly overwrites.

setlocal
set "FLYCAST=D:\Arcade\build\flycast"
set "PATH=D:\msys64\ucrt64\bin;%PATH%"
where gcc || exit /b 1
where cmake || exit /b 1

cd /d "%FLYCAST%" || exit /b 1
cmake -S . -B bld -G Ninja -DLIBRETRO=ON -DUSE_LIBCDIO=ON -DCMAKE_BUILD_TYPE=Release
if errorlevel 1 exit /b 1
cmake --build bld --target flycast_libretro --parallel 14
if errorlevel 1 exit /b 1

copy /y "%FLYCAST%\bld\flycast_libretro.dll" "%FLYCAST%\flycast_custom_libretro.dll"
echo.
echo Built: %FLYCAST%\flycast_custom_libretro.dll
dir "%FLYCAST%\flycast_custom_libretro.dll" | find "flycast_custom"
endlocal
