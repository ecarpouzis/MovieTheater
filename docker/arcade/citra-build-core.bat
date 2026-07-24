@echo off
rem Builds citra_custom_libretro.dll from D:\Arcade\build\citra (github.com/libretro/citra) with the
rem MovieTheater graphics-api patch: TWO lines in src/citra_libretro/citra_libretro.cpp are un-commented
rem   - retro_variable values[]:  {"citra_graphics_api","Graphics API (restart); Auto|Vulkan|OpenGL"}
rem   - UpdateSettings():          auto graphicsApi = LibRetro::FetchVariable("citra_graphics_api","Auto");
rem Stock hardcodes graphicsApi="OpenGL" so citra ALWAYS requests RETRO_HW_CONTEXT_OPENGL_CORE (which our
rem WGL stack cannot host - GL 3.3 ctx then null-calls a GL 4.x fn, 0xC0000005). With the patch, "Auto"
rem honors the frontend GET_PREFERRED_HW_RENDER (our worker answers VULKAN when hwContext:"vulkan"), so
rem citra runs renderer_vulkan on our proven vkm2 Vulkan-capture path. Config sets citra_graphics_api:"Vulkan".
rem Deploy KEEPS the custom name citra_custom_libretro.dll (pins vs libretro.cores.repo.sync overwrite).
rem Toolchain = MSVC (dolphin/lrps2 pattern); citra builds all externals from source (no prebuilt .libs),
rem /MD runtime (VCRUNTIME140 present on Ziggy). QT off (not installed); SDL2 off (libretro uses own window).
rem CMAKE_POLICY_VERSION_MINIMUM=3.5 REQUIRED: vendored externals (xbyak, etc.) declare ancient minimums
rem that CMake 4.x refuses (same fix as the LRPS2 build). Pass "buildonly" to skip reconfigure.
set "PATH=%PATH%;C:\Program Files (x86)\Microsoft Visual Studio\Installer"
call "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\Tools\VsDevCmd.bat" -arch=x64 -host_arch=x64 -no_logo
if errorlevel 1 exit /b 1
where cl || exit /b 1
set "CMAKE=C:\Users\Atoramos\AppData\Roaming\Python\Python313\site-packages\cmake\data\bin\cmake.exe"
set "PATH=%PATH%;C:\Users\Atoramos\AppData\Roaming\Python\Python313\Scripts"
cd /d D:\Arcade\build\citra
if not "%1"=="buildonly" (
  "%CMAKE%" -S . -B bld -G Ninja -DCMAKE_BUILD_TYPE=Release -DCMAKE_C_COMPILER=cl -DCMAKE_CXX_COMPILER=cl -DCMAKE_POLICY_VERSION_MINIMUM=3.5 -DENABLE_LIBRETRO=ON -DENABLE_QT=OFF -DENABLE_SDL2=OFF -DUSE_DISCORD_PRESENCE=OFF -DCITRA_WARNINGS_AS_ERRORS=OFF
  if errorlevel 1 exit /b 1
)
"%CMAKE%" --build bld --target citra_libretro --parallel 14
