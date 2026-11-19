namespace Cafe.Agent.Native;

/// <summary>Enumerates monitors through Win32 (no WinForms reference) and returns their rectangles in physical pixels.</summary>
internal static class MonitorEnumerator
{
    public readonly record struct MonitorRect(int Left, int Top, int Width, int Height);

    public static IReadOnlyList<MonitorRect> GetMonitorRects()
    {
        var result = new List<MonitorRect>();
        NativeMethods.MonitorEnumProc callback = (IntPtr hMonitor, IntPtr hdc, ref NativeMethods.RECT rect, IntPtr data) =>
        {
            var info = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
            if (NativeMethods.GetMonitorInfo(hMonitor, ref info))
            {
                var r = info.rcMonitor;
                result.Add(new MonitorRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top));
            }

            return true;
        };

        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return result;
    }
}
