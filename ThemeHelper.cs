using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TreeDump
{
    public static class ThemeHelper
    {
        private const int DwmwaUseImmersiveDarkMode = 20;
        private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(
            IntPtr hwnd,
            int attr,
            ref int attrValue,
            int attrSize);

        public static void SetDarkTitleBar(Window window, bool isDark)
        {
            if (window == null)
                return;

            try
            {
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero)
                    return;

                int useDark = isDark ? 1 : 0;
                int hr = DwmSetWindowAttribute(
                    hwnd,
                    DwmwaUseImmersiveDarkMode,
                    ref useDark,
                    sizeof(int));

                if (hr != 0)
                {
                    DwmSetWindowAttribute(
                        hwnd,
                        DwmwaUseImmersiveDarkModeBefore20H1,
                        ref useDark,
                        sizeof(int));
                }
            }
            catch
            {
                // Ignore DWM interop failures on unsupported OS versions
            }
        }
    }
}