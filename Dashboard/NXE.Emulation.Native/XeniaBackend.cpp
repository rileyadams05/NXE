#include <filesystem>
#include <memory>

#include "xenia/emulator.h"
#include "xenia/apu/xaudio2/xaudio2_audio_system.h"
#include "xenia/gpu/d3d12/d3d12_graphics_system.h"
#include "xenia/hid/xinput/xinput_hid.h"
#include "XeniaBackend.h"

namespace nxe::native
{
class XeniaBackend
{
public:
    bool Start(const std::filesystem::path& gamePath)
    {
        if (gamePath.empty())
            return false;
        if (!emulator_)
        {
            const auto root = std::filesystem::path(L"\\NXE\\Xenia");
            emulator_ = std::make_unique<xe::Emulator>(
                std::filesystem::path(L"NXE"), root, root / L"content", root / L"cache");
            const auto setup = emulator_->Setup(
                nullptr, nullptr, true,
                [](xe::cpu::Processor* processor) {
                    return xe::apu::xaudio2::XAudio2AudioSystem::Create(processor);
                },
                []() {
                    return std::unique_ptr<xe::gpu::GraphicsSystem>(
                        new xe::gpu::d3d12::D3D12GraphicsSystem());
                },
                [](xe::ui::Window* window) {
                    std::vector<std::unique_ptr<xe::hid::InputDriver>> drivers;
                    drivers.emplace_back(xe::hid::xinput::Create(window, 0));
                    return drivers;
                });
            if (setup != 0)
                return false;
        }
        return emulator_->LaunchPath(gamePath) == 0;
    }

    void Stop()
    {
        if (emulator_)
            emulator_->TerminateTitle();
    }

private:
    std::unique_ptr<xe::Emulator> emulator_;
};

XeniaBackend& Instance()
{
    static XeniaBackend instance;
    return instance;
}

bool XeniaAvailable() { return true; }
bool XeniaStart(const std::filesystem::path& gamePath) { return Instance().Start(gamePath); }
void XeniaStop() { Instance().Stop(); }
}
