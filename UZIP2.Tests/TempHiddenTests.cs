using System;
using System.IO;
using UZIP2.Services;
using Xunit;

namespace UZIP2.Tests
{
    // 隐藏属性是 Windows 概念；Linux 上对目录设 Hidden 会抛 PlatformNotSupportedException，
    // 那会把"建临时目录"整个搞挂，所以按平台分流。
    public class TempHiddenTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "uzip2-temp-" + Guid.NewGuid().ToString("N"));

        public TempHiddenTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        [Fact]
        public void Non_windows_skips_the_hidden_attribute_without_throwing()
        {
            TempManager.ApplyHidden(_dir, isWindows: false);
            Assert.True(Directory.Exists(_dir));
        }

        [Fact]
        public void Windows_sets_the_hidden_attribute()
        {
            TempManager.ApplyHidden(_dir, isWindows: true);
            var attrs = new DirectoryInfo(_dir).Attributes;
            Assert.True((attrs & FileAttributes.Hidden) != 0, "Windows 上应设置隐藏属性");
        }

        [Fact]
        public void Missing_directory_is_not_an_error()
            => TempManager.ApplyHidden(Path.Combine(_dir, "nope"), isWindows: true);
    }
}
