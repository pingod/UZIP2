using UZIP2.Services;
using Xunit;

namespace UZIP2.Tests
{
    // portal 的输出是 gdbus 的文本；解析单独测，真正的注册在有 portal 的桌面上跑。
    public class PortalOutputTests
    {
        [Fact]
        public void Parses_the_session_object_path()
        {
            const string output = "(objectpath '/org/freedesktop/portal/desktop/session/1_42/uzip2_7f3',)\n";
            Assert.Equal("/org/freedesktop/portal/desktop/session/1_42/uzip2_7f3",
                PortalOutput.ParseSessionPath(output));
        }

        [Fact]
        public void Returns_null_when_the_output_is_not_a_session()
        {
            Assert.Null(PortalOutput.ParseSessionPath("(error: portal 不存在)"));
            Assert.Null(PortalOutput.ParseSessionPath(""));
            Assert.Null(PortalOutput.ParseSessionPath(null));
        }

        [Fact]
        public void Detects_activation_for_our_shortcut_only()
        {
            const string line = "/org/freedesktop/portal/desktop: org.freedesktop.portal.GlobalShortcuts.Activated " +
                                "(objectpath '/org/freedesktop/portal/desktop/session/1_42/u', 'toggle', uint64 123, {})";
            Assert.True(PortalOutput.IsActivated(line, "toggle"));
            Assert.False(PortalOutput.IsActivated(line, "other"));
            Assert.False(PortalOutput.IsActivated("unrelated output", "toggle"));
        }

        [Fact]
        public void Ignores_other_portal_signals()
        {
            const string line = "/org/freedesktop/portal/desktop: org.freedesktop.portal.GlobalShortcuts.ShortcutsChanged " +
                                "(objectpath '/org/freedesktop/portal/desktop/session/1_42/u', [('toggle', {})])";
            Assert.False(PortalOutput.IsActivated(line, "toggle"));
        }
    }
}
