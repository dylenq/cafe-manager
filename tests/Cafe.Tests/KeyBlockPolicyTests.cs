using Cafe.Agent.Core;

namespace Cafe.Tests;

public class KeyBlockPolicyTests
{
    [Theory]
    [InlineData(KeyBlockPolicy.VkLWin, false, false, false)]
    [InlineData(KeyBlockPolicy.VkRWin, false, false, false)]
    [InlineData(KeyBlockPolicy.VkTab, true, false, false)]
    [InlineData(KeyBlockPolicy.VkEscape, true, false, false)]
    [InlineData(KeyBlockPolicy.VkEscape, false, true, false)]
    [InlineData(KeyBlockPolicy.VkF4, true, false, false)]
    [InlineData(KeyBlockPolicy.VkEscape, false, true, true)]
    public void Escape_chords_are_blocked(int vk, bool alt, bool ctrl, bool shift)
    {
        Assert.True(KeyBlockPolicy.ShouldBlock(vk, alt, ctrl, shift));
    }

    [Theory]
    [InlineData(KeyBlockPolicy.VkTab, false, false, false)]
    [InlineData(KeyBlockPolicy.VkEscape, false, false, false)]
    [InlineData(KeyBlockPolicy.VkF4, false, false, false)]
    [InlineData(0x43, false, true, false)] // Ctrl+C
    [InlineData(0x12, true, false, false)] // Alt alone (VK_MENU)
    [InlineData(KeyBlockPolicy.VkF12, true, true, true)] // emergency chord
    public void Ordinary_keys_and_the_emergency_chord_are_not_blocked(int vk, bool alt, bool ctrl, bool shift)
    {
        Assert.False(KeyBlockPolicy.ShouldBlock(vk, alt, ctrl, shift));
    }

    [Theory]
    [InlineData(KeyBlockPolicy.VkF12, true, true, true, true)]
    [InlineData(KeyBlockPolicy.VkF12, true, true, false, false)]
    [InlineData(KeyBlockPolicy.VkF12, false, true, true, false)]
    [InlineData(KeyBlockPolicy.VkF12, true, false, true, false)]
    [InlineData(KeyBlockPolicy.VkF4, true, true, true, false)]
    public void Emergency_chord_needs_f12_and_all_three_modifiers(int vk, bool ctrl, bool alt, bool shift, bool expected)
    {
        Assert.Equal(expected, KeyBlockPolicy.IsEmergencyChord(vk, ctrl, alt, shift));
    }
}
