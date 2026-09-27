#pragma once
#include <filesystem>

namespace nxe::native
{
bool XeniaAvailable();
bool XeniaStart(const std::filesystem::path& gamePath);
void XeniaStop();
}
