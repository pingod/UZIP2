using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using UZIP2.Services;
using Xunit;

namespace UZIP2.Tests
{
    // Linux 没有注册表：右键菜单 = 往文件管理器自己的目录里写脚本/菜单文件。
    // 用临时 HOME 验证注册、状态、移除，不碰真实用户目录。
    public class LinuxShellIntegrationTests : IDisposable
    {
        private readonly string _home;
        private readonly string _exe;

        public LinuxShellIntegrationTests()
        {
            _home = Path.Combine(Path.GetTempPath(), "uzip2-home-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_home);
            _exe = Path.Combine(_home, "bin", "uzip2");
        }

        public void Dispose()
        {
            try { Directory.Delete(_home, true); } catch { }
        }

        private LinuxShellIntegration New() => new LinuxShellIntegration(_home);

        [Fact]
        public void Register_writes_nautilus_scripts_and_a_dolphin_service_menu()
        {
            using var svc = New();
            svc.Register(_exe);

            var scripts = Path.Combine(_home, ".local", "share", "nautilus", "scripts");
            Assert.True(File.Exists(Path.Combine(scripts, "UZIP2 解压到当前文件夹")));
            Assert.True(File.Exists(Path.Combine(scripts, "UZIP2 压缩")));

            var desktop = Path.Combine(_home, ".local", "share", "kio", "servicemenus", "uzip2.desktop");
            Assert.True(File.Exists(desktop));
            var text = File.ReadAllText(desktop);
            Assert.Contains("[Desktop Action extractHere]", text);
            Assert.Contains(_exe, text);
        }

        [Fact]
        public void Scripts_call_the_registered_executable_with_shell_quoting()
        {
            using var svc = New();
            svc.Register(_exe);
            var script = Path.Combine(_home, ".local", "share", "nautilus", "scripts", "UZIP2 解压到当前文件夹");
            var text = File.ReadAllText(script);
            Assert.StartsWith("#!/bin/sh", text);
            Assert.Contains("\"", text);              // 路径必须加引号，空格路径才不会裂开
            Assert.Contains("extract --here", text);
            Assert.Contains("$@", text);
        }

        [Fact]
        public void State_is_current_right_after_register()
        {
            using var svc = New();
            svc.Register(_exe);
            Assert.Equal(ShellMenuState.Current, svc.State(_exe));
        }

        [Fact]
        public void State_is_stale_when_entries_point_at_another_build()
        {
            using var svc = New();
            svc.Register(_exe);
            Assert.Equal(ShellMenuState.Stale, svc.State(Path.Combine(_home, "bin", "uzip2-new")));
        }

        [Fact]
        public void State_is_missing_before_register()
        {
            using var svc = New();
            Assert.Equal(ShellMenuState.Missing, svc.State(_exe));
        }

        [Fact]
        public void Unregister_removes_only_entries_we_wrote()
        {
            using var svc = New();
            var scripts = Path.Combine(_home, ".local", "share", "nautilus", "scripts");
            Directory.CreateDirectory(scripts);
            var foreign = Path.Combine(scripts, "别的脚本");
            File.WriteAllText(foreign, "#!/bin/sh\necho hi\n");

            svc.Register(_exe);
            svc.Unregister();

            Assert.False(File.Exists(Path.Combine(scripts, "UZIP2 压缩")));
            Assert.True(File.Exists(foreign), "不是我们写的脚本不能删");
            Assert.Equal(ShellMenuState.Missing, svc.State(_exe));
        }

        private string UcaPath => Path.Combine(_home, ".config", "Thunar", "uca.xml");

        private void SeedUserThunarAction()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(UcaPath));
            File.WriteAllText(UcaPath,
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                "<actions><action><unique-id>user-one</unique-id><name>用户自己的动作</name>" +
                "<command>echo hi</command></action></actions>");
        }

        private static System.Collections.Generic.List<string> Ids(string path)
            => XDocument.Load(path).Descendants("unique-id").Select(e => e.Value).ToList();

        [Fact]
        public void Register_merges_thunar_actions_and_keeps_the_users_own()
        {
            SeedUserThunarAction();
            using var svc = New();
            svc.Register(_exe);

            var ids = Ids(UcaPath);
            Assert.Contains("user-one", ids);
            Assert.Contains("uzip2-extract-here", ids);
            Assert.Contains("uzip2-compress", ids);
            Assert.True(File.Exists(UcaPath + ".uzip2.bak"), "改用户的 uca.xml 前必须留备份");
            Assert.Contains(_exe, File.ReadAllText(UcaPath));
        }

        [Fact]
        public void Register_is_idempotent_for_thunar()
        {
            using var svc = New();
            svc.Register(_exe);
            svc.Register(_exe);
            var ids = Ids(UcaPath);
            Assert.Single(ids.Where(i => i == "uzip2-extract-here"));
            Assert.Single(ids.Where(i => i == "uzip2-compress"));
        }

        [Fact]
        public void Unregister_leaves_the_thunar_file_and_user_actions_intact()
        {
            SeedUserThunarAction();
            using var svc = New();
            svc.Register(_exe);
            svc.Unregister();

            var ids = Ids(UcaPath);
            Assert.Equal(new[] { "user-one" }, ids);
            Assert.False(File.Exists(Path.Combine(_home, ".local", "share", "nautilus", "scripts", "UZIP2 压缩")));
        }

        [Fact]
        public void State_turns_stale_when_thunar_entries_point_elsewhere()
        {
            using var svc = New();
            svc.Register(_exe);
            File.WriteAllText(UcaPath, File.ReadAllText(UcaPath).Replace(_exe, "/opt/other/uzip2"));
            Assert.Equal(ShellMenuState.Stale, svc.State(_exe));
        }

        [Fact]
        public void Probe_reports_the_executable_written_into_the_entries()
        {
            using var svc = New();
            svc.Register(_exe);
            Assert.Equal(_exe, svc.ProbeCommand());
        }
    }
}
