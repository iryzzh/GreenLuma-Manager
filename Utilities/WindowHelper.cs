using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace GreenLuma_Manager.Utilities;

public static class WindowHelper
{
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    public static void EnableWindows11Style(Window window)
    {
        if (Environment.OSVersion.Version.Build >= 22000)
        {
            var helper = new WindowInteropHelper(window);
            if (helper.Handle != IntPtr.Zero)
            {
                ApplyDwmAttributes(helper.Handle);
            }
            else
            {
                window.SourceInitialized += (_, _) =>
                {
                    var hwnd = new WindowInteropHelper(window).Handle;
                    if (hwnd != IntPtr.Zero)
                        ApplyDwmAttributes(hwnd);
                };
            }
        }
    }

    private static void ApplyDwmAttributes(IntPtr hwnd)
    {
        try
        {
            var corner = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

            var darkMode = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
        }
        catch
        {
        }
    }
}
