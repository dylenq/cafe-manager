namespace Cafe.Agent.Core;

/// <summary>
/// Pure decision of which key chords the lock screen swallows. Platform-neutral so it is unit-tested on any OS;
/// the Windows keyboard hook only supplies the virtual-key code and modifier state.
/// </summary>
public static class KeyBlockPolicy
{
    public const int VkTab = 0x09;
    public const int VkEscape = 0x1B;
    public const int VkF4 = 0x73;
    public const int VkF12 = 0x7B;
    public const int VkLWin = 0x5B;
    public const int VkRWin = 0x5C;

    /// <summary>Ctrl+Alt+Shift+F12 opens the emergency unlock prompt.</summary>
    public static bool IsEmergencyChord(int vkCode, bool ctrlDown, bool altDown, bool shiftDown) =>
        vkCode == VkF12 && ctrlDown && altDown && shiftDown;

    /// <summary>
    /// True when the key press must be swallowed while locked: Win keys, Alt+Tab, Alt+Esc, Ctrl+Esc, Alt+F4 and
    /// Ctrl+Shift+Esc (Task Manager). The emergency chord is never blocked.
    /// </summary>
    public static bool ShouldBlock(int vkCode, bool altDown, bool ctrlDown, bool shiftDown)
    {
        if (IsEmergencyChord(vkCode, ctrlDown, altDown, shiftDown))
        {
            return false;
        }

        return vkCode switch
        {
            VkLWin or VkRWin => true,
            VkTab => altDown,
            VkEscape => altDown || ctrlDown,
            VkF4 => altDown,
            _ => false,
        };
    }
}
