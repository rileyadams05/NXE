#include "NativeBridge.h"
#include <filesystem>
#include <string>
#include <windows.h>
#include <atomic>
#include <thread>
#include <fstream>
#include <vector>
#include "XeniaBackend.h"
#include "D:\\Projects\\NXE\\Assets\\NXE\\EMU\\flycast\\core\\deps\\libretro-common\\include\\libretro.h"

namespace
{
    std::wstring g_backend;
    std::wstring g_path;
    HMODULE g_core = nullptr;
    std::thread g_coreThread;
    std::atomic_bool g_coreRunning{ false };

    using retro_init_t = void (*)();
    using retro_deinit_t = void (*)();
    using retro_api_version_t = unsigned (*)();
    using retro_set_environment_t = void (*)(retro_environment_t);
    using retro_set_video_refresh_t = void (*)(retro_video_refresh_t);
    using retro_set_audio_sample_t = void (*)(retro_audio_sample_t);
    using retro_set_audio_sample_batch_t = void (*)(retro_audio_sample_batch_t);
    using retro_set_input_poll_t = void (*)(retro_input_poll_t);
    using retro_set_input_state_t = void (*)(retro_input_state_t);
    using retro_load_game_t = bool (*)(const retro_game_info*);
    using retro_unload_game_t = void (*)();
    using retro_run_t = void (*)();

    retro_environment_t g_environment = [](unsigned, void*) -> bool { return false; };
    retro_video_refresh_t g_video = [](const void*, unsigned, unsigned, size_t) {};
    retro_audio_sample_t g_audio = [](int16_t, int16_t) {};
    retro_audio_sample_batch_t g_audioBatch = [](const int16_t*, size_t frames) -> size_t { return frames; };
    retro_input_poll_t g_inputPoll = []() {};
    retro_input_state_t g_inputState = [](unsigned, unsigned, unsigned, unsigned) -> int16_t { return 0; };

    bool LoadLibretroCore(const wchar_t* backendId)
    {
        if (!backendId || _wcsicmp(backendId, L"flycast") != 0)
            return false;
        if (g_core)
            return true;
        g_core = LoadPackagedLibrary(L"Cores\\flycast_libretro.dll", 0);
        if (!g_core)
            return false;
        const auto init = reinterpret_cast<retro_init_t>(GetProcAddress(g_core, "retro_init"));
        const auto deinit = reinterpret_cast<retro_deinit_t>(GetProcAddress(g_core, "retro_deinit"));
        const auto api = reinterpret_cast<retro_api_version_t>(GetProcAddress(g_core, "retro_api_version"));
        if (!init || !deinit || !api || api() == 0)
        {
            FreeLibrary(g_core);
            g_core = nullptr;
            return false;
        }
        return true;
    }

    bool StartLibretro(const wchar_t* backendId, const wchar_t* gamePath)
    {
        if (!LoadLibretroCore(backendId) || g_coreThread.joinable())
            return false;
        auto setEnvironment = reinterpret_cast<retro_set_environment_t>(GetProcAddress(g_core, "retro_set_environment"));
        auto setVideo = reinterpret_cast<retro_set_video_refresh_t>(GetProcAddress(g_core, "retro_set_video_refresh"));
        auto setAudio = reinterpret_cast<retro_set_audio_sample_t>(GetProcAddress(g_core, "retro_set_audio_sample"));
        auto setBatch = reinterpret_cast<retro_set_audio_sample_batch_t>(GetProcAddress(g_core, "retro_set_audio_sample_batch"));
        auto setPoll = reinterpret_cast<retro_set_input_poll_t>(GetProcAddress(g_core, "retro_set_input_poll"));
        auto setState = reinterpret_cast<retro_set_input_state_t>(GetProcAddress(g_core, "retro_set_input_state"));
        auto init = reinterpret_cast<retro_init_t>(GetProcAddress(g_core, "retro_init"));
        auto load = reinterpret_cast<retro_load_game_t>(GetProcAddress(g_core, "retro_load_game"));
        auto run = reinterpret_cast<retro_run_t>(GetProcAddress(g_core, "retro_run"));
        auto unload = reinterpret_cast<retro_unload_game_t>(GetProcAddress(g_core, "retro_unload_game"));
        if (!setEnvironment || !setVideo || !setAudio || !setBatch || !setPoll || !setState || !init || !load || !run || !unload)
            return false;
        setEnvironment(g_environment);
        setVideo(g_video);
        setAudio(g_audio);
        setBatch(g_audioBatch);
        setPoll(g_inputPoll);
        setState(g_inputState);
        init();
        std::string path;
        int length = WideCharToMultiByte(CP_UTF8, 0, gamePath, -1, nullptr, 0, nullptr, nullptr);
        if (length <= 1)
            return false;
        path.resize(static_cast<size_t>(length));
        WideCharToMultiByte(CP_UTF8, 0, gamePath, -1, path.data(), length, nullptr, nullptr);
        path.pop_back();
        retro_game_info info{};
        info.path = path.c_str();
        if (!load(&info))
        {
            unload();
            return false;
        }
        g_coreRunning = true;
        g_coreThread = std::thread([run, unload]()
        {
            while (g_coreRunning)
                run();
            unload();
        });
        return true;
    }

    bool IsXenia(const wchar_t* backendId)
    {
        return backendId && _wcsicmp(backendId, L"xenia") == 0;
    }
}

int __cdecl NXE_EmulationNative_GetBackendStatus(const wchar_t* backendId)
{
    if (IsXenia(backendId))
        return nxe::native::XeniaAvailable() ? 1 : 0;
    return LoadLibretroCore(backendId) ? 1 : 0;
}

int __cdecl NXE_EmulationNative_Start(const wchar_t* backendId, const wchar_t* gamePath)
{
    if (!backendId || !*backendId || !gamePath || !*gamePath)
        return 0;
    if (IsXenia(backendId))
        return nxe::native::XeniaStart(std::filesystem::path(gamePath)) ? 1 : 0;
    if (!StartLibretro(backendId, gamePath))
        return 0;
    g_backend = backendId;
    g_path = gamePath;
    return 1;
}

void __cdecl NXE_EmulationNative_Stop()
{
    g_backend.clear();
    g_path.clear();
    g_coreRunning = false;
    if (g_coreThread.joinable())
        g_coreThread.join();
    nxe::native::XeniaStop();
    if (g_core)
    {
        FreeLibrary(g_core);
        g_core = nullptr;
    }
}
