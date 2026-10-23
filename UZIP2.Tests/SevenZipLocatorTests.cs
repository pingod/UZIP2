using System.IO;
using UZIP2.Services;
using Xunit;

namespace UZIP2.Tests
{
    // Linux 上 7-Zip 的常见落点：发行版包（/usr/bin/7z、p7zip 的 7za、官方 7zz），
    // 以及 PATH 里自带的一份。Windows 保持原来的 Program Files 两个候选。
    public class SevenZipLocatorTests
    {
        const string Base = "/opt/uzip2";

        [Fact]
        public void Linux_prefers_the_bundled_copy_then_usr_bin_7zz_then_7z()
        {
            var c = SevenZipLocator.Candidates(Base, isWindows: false,
                programFiles: "", programFilesX86: "", pathEnvironment: "");

            Assert.Equal(Path.Combine(Base, "7-Zip", "7z"), c[0]);
            Assert.Equal("/usr/bin/7zz", c[1]);
            Assert.Equal("/usr/bin/7z", c[2]);
        }

        [Fact]
        public void Linux_covers_the_packaged_install_locations()
        {
            var c = SevenZipLocator.Candidates(Base, false, "", "", "");
            Assert.Contains("/usr/bin/7za", c);
            Assert.Contains("/usr/lib/p7zip/7z", c);
            Assert.Contains("/usr/lib/7zip/7z", c);
        }

        [Fact]
        public void Linux_scans_PATH_entries_after_the_fixed_locations()
        {
            var path = "/home/me/bin" + Path.PathSeparator + "/opt/tools/" + Path.PathSeparator;
            var c = SevenZipLocator.Candidates(Base, false, "", "", path);
            Assert.Contains(Path.Combine("/home/me/bin", "7z"), c);
            Assert.Contains(Path.Combine("/opt/tools", "7zz"), c);
            // 固定在 /usr/bin 之前，PATH 里的同名项不能把它挤掉
            Assert.True(Index(c, "/usr/bin/7zz") < Index(c, Path.Combine("/home/me/bin", "7zz")));
        }

        static int Index(System.Collections.Generic.IReadOnlyList<string> list, string value)
        {
            for (int i = 0; i < list.Count; i++)
                if (list[i] == value) return i;
            return -1;
        }

        [Fact]
        public void Empty_PATH_segments_are_ignored()
        {
            var path = Path.PathSeparator.ToString() + Path.PathSeparator;
            var c = SevenZipLocator.Candidates(Base, false, "", "", path);
            Assert.DoesNotContain(c, p => p == Path.Combine("", "7z") || p == "7z");
        }

        [Fact]
        public void Windows_still_looks_in_program_files()
        {
            var c = SevenZipLocator.Candidates(@"C:\apps", true,
                @"C:\Program Files", @"C:\Program Files (x86)", "");
            Assert.Equal(Path.Combine(@"C:\apps", "7-Zip", "7z.exe"), c[0]);
            Assert.Contains(Path.Combine(@"C:\Program Files", "7-Zip", "7z.exe"), c);
            Assert.Contains(Path.Combine(@"C:\Program Files (x86)", "7-Zip", "7z.exe"), c);
            Assert.DoesNotContain("/usr/bin/7zz", c);
        }

        [Fact]
        public void Duplicates_are_collapsed()
        {
            var path = "/usr/bin" + Path.PathSeparator + "/usr/bin";
            var c = SevenZipLocator.Candidates(Base, false, "", "", path);
            var occurrences = 0;
            foreach (var p in c) if (p == "/usr/bin/7z") occurrences++;
            Assert.Equal(1, occurrences);
        }
    }
}
