@echo off
rem Builds citra_custom_libretro.dll from D:\Arcade\build\citra (github.com/libretro/citra) with the
rem MovieTheater patch set in docker\arcade\citra-msvc-vs18.patch. Four functional changes on top of
rem the MSVC build fixes (all verified live 2026-07-24 on MK7):
rem  1. citra_graphics_api option un-commented (values[] + UpdateSettings FetchVariable). Stock
rem     hardcodes graphicsApi="OpenGL" so citra ALWAYS requests RETRO_HW_CONTEXT_OPENGL_CORE (which our
rem     WGL stack cannot host - GL 3.3 ctx then null-calls a GL 4.x fn, 0xC0000005). With the patch,
rem     "Auto" honors GET_PREFERRED_HW_RENDER (our worker answers VULKAN when hwContext:"vulkan"), so
rem     citra runs renderer_vulkan on our vkm2 Vulkan-capture path. Config sets citra_graphics_api:"Vulkan".
rem  2. vk_create_device now forwards the frontend's required_device_extensions into
rem     Vulkan::Instance::CreateDevice (new SetExtraDeviceExtensions). Stock DROPPED them, so the device
rem     had no VK_KHR_external_memory_win32/_semaphore_win32 and our zero-copy capture could not export
rem     (vkGetMemoryWin32HandleKHR == NULL). NOTE: enabled_extensions static_vector 13 -> 24.
rem  3. vk_present_window post-barrier ends in eShaderReadOnlyOptimal, not ePresentSrcKHR - the libretro
rem     "swapchain" is a set_image handoff that ADVERTISES SHADER_READ_ONLY_OPTIMAL; the mismatch let the
rem     driver discard contents.
rem  4. retro_get_system_av_info max geometry = base (400x480) instead of base*10. The frontend sizes its
rem     synthetic VkSurfaceKHR from max and then reads back only base, so 10x meant capturing the
rem     top-left 1%. COUPLED: raise both if citra_resolution_factor ever goes above 1x.
rem Also present (env-gated, OFF by default) are three black-screen diagnostics: CITRA_MT_CLEAR_TEST,
rem CITRA_MT_GREEN_CLEAR, CITRA_MT_NO_ACCEL_DISPLAY - see the memory note for what each one proves.
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
  rem /vmg IS LOAD-BEARING, NOT AN OPTIMISATION — without it every HLE service in the emulator is
  rem silently half-broken and games die on boot with a 3DS fatal error (black screen).
  rem Why: ServiceFramework<Self>::RegisterHandlers passes a `const FunctionInfo*` (which holds a
  rem HandlerFnP<Self>) to RegisterHandlersBase(const FunctionInfoBase*), which walks it with
  rem FunctionInfoBase stride. Under MSVC a pointer-to-member's SIZE depends on the class's
  rem inheritance model, so HandlerFnP<SRV> is wider than HandlerFnP<ServiceFrameworkBase>, the two
  rem structs differ in size, and the walk reads garbage command ids (srv: registered 14 handlers and
  rem the flat_map ended up with 7 junk keys - 0x0000..0x0004, 0x7ffe, 0x9a54cef8 - so
  rem GetServiceHandle(0x0005) missed and the guest fatal-errored on APT:U). The libretro buildbot
  rem never hits this because GCC's Itanium ABI gives every member pointer one fixed size.
  rem /vmg forces the most-general (uniform) representation, making the two layouts agree.
  rem MUST be applied to EVERY TU - it is an ABI flag - hence CMAKE_CXX_FLAGS, not a per-target option.
  "%CMAKE%" -S . -B bld -G Ninja -DCMAKE_BUILD_TYPE=Release -DCMAKE_C_COMPILER=cl -DCMAKE_CXX_COMPILER=cl -DCMAKE_CXX_FLAGS="/vmg" -DCMAKE_POLICY_VERSION_MINIMUM=3.5 -DENABLE_LIBRETRO=ON -DENABLE_QT=OFF -DENABLE_SDL2=OFF -DUSE_DISCORD_PRESENCE=OFF -DCITRA_WARNINGS_AS_ERRORS=OFF
  if errorlevel 1 exit /b 1
)
"%CMAKE%" --build bld --target citra_libretro --parallel 14
