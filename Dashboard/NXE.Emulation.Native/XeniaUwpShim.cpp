#include <windows.h>
#include <string>

namespace
{
bool g_ui_open = false;
}

namespace UWP
{
void DiagnosticLog(const std::string& message)
{
    OutputDebugStringA(("[NXE/XENIA] " + message + "\n").c_str());
}

std::string GetLocalState()
{
    wchar_t buffer[MAX_PATH]{};
    const DWORD length = GetEnvironmentVariableW(L"LOCALAPPDATA", buffer, MAX_PATH);
    if (!length || length >= MAX_PATH)
        return "NXE/Xenia";
    std::wstring value(buffer, length);
    value += L"\\Packages\\NXE\\LocalState\\Xenia";
    return std::string(value.begin(), value.end());
}

void ShowKeyboard() {}
bool IsUIOpen() { return g_ui_open; }
void SetUIOpen(bool value) { g_ui_open = value; }
}
