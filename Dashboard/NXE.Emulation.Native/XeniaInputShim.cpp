#include "xenia-canary-uwp/window_uwp.h"

namespace xe::ui
{
void UWPWindow::SetXInputDriver(xe::hid::xinput::XInputInputDriver* driver)
{
    input_driver = driver;
}

void UWPWindow::ClearXInputDriver()
{
    input_driver = nullptr;
}
}
