using System.Runtime.InteropServices;

namespace AiTranslate.Platform.Windows;

public static class WindowsCopyInput
{
    public static bool IsInjectedShortcut()
    {
        const uint keyboardDevice = 1, injectedOrigin = 2;
        // A Copy command from a context menu or a button is also a valid explicit copy.
        return GetCurrentInputMessageSource(out var source) && source.Device == keyboardDevice && source.Origin == injectedOrigin &&
            (GetKeyState(0x11) & 0x8000) != 0 &&
            ((GetKeyState(0x43) & 0x8000) != 0 || (GetKeyState(0x2D) & 0x8000) != 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InputMessageSource { public uint Device, Origin; }

    [DllImport("user32.dll")]
    private static extern bool GetCurrentInputMessageSource(out InputMessageSource source);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int key);
}
