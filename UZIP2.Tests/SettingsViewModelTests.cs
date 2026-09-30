using UZIP2.ViewModel;
using Xunit;

namespace UZIP2.Tests
{
    public class SettingsViewModelTests
    {
        [Theory]
        [InlineData(0x58, true, true, false, "Ctrl+Alt+X")]
        [InlineData(0x41, false, false, false, "A")]
        [InlineData(0x70, true, false, true, "Ctrl+Shift+F1")]
        [InlineData(0, false, false, false, "未设置")]
        public void FormatHotkey_MatchesLegacyOrdering(uint vk, bool ctrl, bool alt, bool shift, string expected)
        {
            Assert.Equal(expected, SettingsViewModel.FormatHotkey(vk, ctrl, alt, shift));
        }

        [Theory]
        [InlineData("A", 0x41)]
        [InlineData("F12", 0x7B)]
        [InlineData("D9", 0x39)]
        public void VirtualKey_RoundTrips(string keyName, uint expectedVk)
        {
            Assert.Equal(expectedVk, SettingsViewModel.NameToVk(keyName));
        }
    }
}
