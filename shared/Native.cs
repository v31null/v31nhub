using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace Hub
{
    static class Native
    {
        public const int WS_MINIMIZEBOX = 0x00020000, WS_SYSMENU = 0x00080000;
        public const int WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_NOREDIRECTIONBITMAP = 0x00200000, WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20;
        public const int WM_DPICHANGED = 0x02E0, SW_SHOWNOACTIVATE = 4;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hh, uint flags);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(Point p, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromRect(ref Rect r, uint flags);
        [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr m, int type, out uint x, out uint y);

        [StructLayout(LayoutKind.Sequential)]
        public struct Rect
        {
            public int Left, Top, Right, Bottom;
        }

        static double Dpi(IntPtr m)
        {
            try
            {
                if (GetDpiForMonitor(m, 0, out var x, out _) == 0) return x / 96.0;
            }
            catch (Exception) { }
            return 1;
        }

        public static double Scale(Point p) => Dpi(MonitorFromPoint(p, 2));

        public static double Scale(Rectangle b)
        {
            var r = new Rect { Left = b.Left, Top = b.Top, Right = b.Right, Bottom = b.Bottom };
            return Dpi(MonitorFromRect(ref r, 2));
        }
    }

}
