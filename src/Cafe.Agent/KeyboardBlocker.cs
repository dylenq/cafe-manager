using Cafe.Agent.Core;
using Cafe.Agent.Native;

namespace Cafe.Agent;

/// <summary>
/// WH_KEYBOARD_LL hook that swallows the chords KeyBlockPolicy rejects. It is installed only while the PC is
/// locked, so games and normal sessions are never affected. Must be installed from the UI thread.
/// </summary>
internal sealed class KeyboardBlocker : IDisposable
{
    // Kept in a field so the GC cannot collect the delegate while the hook is installed.
    private readonly NativeMethods.LowLevelKeyboardProc _proc;
    private IntPtr _hook = IntPtr.Zero;

    public KeyboardBlocker()
    {
        _proc = HookCallback;
    }

    public bool IsInstalled => _hook != IntPtr.Zero;

    public void Install()
    {
        if (_hook != IntPtr.Zero)
        {
            return;
        }

        _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _proc, NativeMethods.GetModuleHandle(null), 0);
    }

    public void Uninstall()
    {
        if (_hook == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    public void Dispose() => Uninstall();

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode == NativeMethods.HC_ACTION)
            {
                var msg = wParam.ToInt32();
                if (msg is NativeMethods.WM_KEYDOWN or NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYDOWN or NativeMethods.WM_SYSKEYUP)
                {
                    var data = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
                    var alt = (data.flags & NativeMethods.LLKHF_ALTDOWN) != 0;
                    var ctrl = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0;
                    var shift = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_SHIFT) & 0x8000) != 0;
                    if (KeyBlockPolicy.ShouldBlock((int)data.vkCode, alt, ctrl, shift))
                    {
                        return (IntPtr)1;
                    }
                }
            }
        }
        catch (Exception)
        {
            // The hook must never fault the input stack: on any error let the key through.
        }

        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }
}
