using System;
using Microsoft.Win32;
using UZIP2.Services;
using Xunit;

namespace UZIP2.Tests
{
    public class ShellArgsTests
    {
        [Fact]
        public void Bare_paths_default_to_extract()
        {
            var r = ShellArgs.Parse(new[] { @"D:\a.zip", @"D:\b.7z" });
            Assert.Equal(ShellVerb.Extract, r.Verb);
            Assert.Equal(new[] { @"D:\a.zip", @"D:\b.7z" }, r.Files);
            Assert.False(r.RegisterShell);
            Assert.False(r.UnregisterShell);
        }

        [Fact]
        public void Verbs_and_flags_are_recognised_in_any_order()
        {
            var r = ShellArgs.Parse(new[] { @"D:\pack", "--COMPRESS" });
            Assert.Equal(ShellVerb.Compress, r.Verb);
            Assert.Equal(new[] { @"D:\pack" }, r.Files);

            Assert.Equal(ShellVerb.ExtractHere, ShellArgs.Parse(new[] { "--extract-here", "x" }).Verb);
            Assert.True(ShellArgs.Parse(new[] { "--register-shell" }).RegisterShell);
            Assert.True(ShellArgs.Parse(new[] { "--unregister-shell" }).UnregisterShell);
        }

        [Fact]
        public void Quoted_paths_and_unknown_flags_survive()
        {
            // 注册表命令行里带引号的路径；未知开关是旧版本菜单留下的，不能把路径吃掉
            var r = ShellArgs.Parse(new[] { "\"D:\\我的 包.zip\"", "--legacy-flag" });
            Assert.Equal(new[] { @"D:\我的 包.zip" }, r.Files);
        }

        [Fact]
        public void Null_and_empty_args_yield_nothing()
        {
            Assert.Empty(ShellArgs.Parse(null).Files);
            Assert.Empty(ShellArgs.Parse(new[] { "", "  " }).Files);
            Assert.Equal(ShellVerb.Extract, ShellArgs.Parse(new string[0]).Verb);
        }
    }

    // 注册表读写跑在 HKCU 下的一次性测试根键上，绝不碰真的 Software\Classes
    public class ShellMenuServiceTests : IDisposable
    {
        const string FakeExe = @"D:\apps\UZIP\UZIP2.exe";
        readonly string _rootName;

        public ShellMenuServiceTests()
        {
            _rootName = @"Software\UZIP2ShellTests-" + Guid.NewGuid().ToString("N");
        }

        public void Dispose()
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(_rootName, false); } catch { }
        }

        RegistryKey Root() => Registry.CurrentUser.CreateSubKey(_rootName);

        [Fact]
        public void Register_then_state_is_current_and_other_exe_is_stale()
        {
            using var root = Root();
            using var svc = new ShellMenuService(root);
            Assert.Equal(ShellMenuState.Missing, svc.State(FakeExe));

            svc.Register(FakeExe);
            Assert.Equal(ShellMenuState.Current, svc.State(FakeExe));
            Assert.Equal(ShellMenuState.Stale, svc.State(@"D:\old\UZIP2.exe"));
            Assert.Equal(ShellMenuState.Missing, svc.State(null));
        }

        [Fact]
        public void Commands_carry_the_right_verb_and_placeholder()
        {
            using var root = Root();
            using var svc = new ShellMenuService(root);
            svc.Register(FakeExe);

            Assert.Equal("\"D:\\apps\\UZIP\\UZIP2.exe\" --extract-here \"%1\"",
                root.OpenSubKey(@"*\shell\UZIP.ExtractHere\command").GetValue(""));
            Assert.Equal("\"D:\\apps\\UZIP\\UZIP2.exe\" --compress \"%V\"",
                root.OpenSubKey(@"Directory\Background\shell\UZIP.Compress\command").GetValue(""));
            Assert.Equal("用 UZIP 解压", root.OpenSubKey(@"*\shell\UZIP.Extract").GetValue(""));
            Assert.Contains("UZIP2.exe", (string)root.OpenSubKey(@"*\shell\UZIP.Compress").GetValue("Icon"));
        }

        [Fact]
        public void Register_is_idempotent_and_unregister_removes_every_entry()
        {
            using var root = Root();
            using var svc = new ShellMenuService(root);
            svc.Register(FakeExe);
            svc.Register(FakeExe);
            Assert.Equal(ShellMenuState.Current, svc.State(FakeExe));

            svc.Unregister();
            Assert.Equal(ShellMenuState.Missing, svc.State(FakeExe));
            foreach (var e in ShellMenuService.Menu)
                Assert.Null(root.OpenSubKey(e.Key + @"\command"));
        }

        [Fact]
        public void Unregister_is_safe_when_nothing_was_registered()
        {
            using var root = Root();
            using var svc = new ShellMenuService(root);
            var ex = Record.Exception(() => svc.Unregister());
            Assert.Null(ex);
        }

        [Fact]
        public void Moving_the_exe_makes_the_registration_stale()
        {
            using var root = Root();
            using var svc = new ShellMenuService(root);
            svc.Register(FakeExe);
            svc.Register(@"D:\newer\UZIP2.exe");
            Assert.Equal(ShellMenuState.Current, svc.State(@"D:\newer\UZIP2.exe"));
            Assert.Equal(ShellMenuState.Stale, svc.State(FakeExe));
            Assert.Contains("newer", svc.ProbeCommand());
        }
    }
}
