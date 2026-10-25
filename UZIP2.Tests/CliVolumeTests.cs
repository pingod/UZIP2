using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UZIP2.Cli;
using UZIP2.Services;
using Xunit;

namespace UZIP2.Tests
{
    // CLI 对分卷的处理：中间卷回落到主卷、批量卷去重、缺卷诊断。
    // 用真 7z 造小分卷（16k 一片），所以这些用例覆盖的是真实 7z 的报错文本。
    public class CliVolumeTests : IAsyncLifetime, IDisposable
    {
        string _root = "";
        string _base = "";
        string _config = "";
        string _src = "";
        string _vol = "";        // 完整卷组所在目录
        string _set = "";        // 主卷路径 z7.7z.001

        public async Task InitializeAsync()
        {
            _root = Path.Combine(Path.GetTempPath(), "UZipCliVol_" + Guid.NewGuid().ToString("N"));
            _base = Path.Combine(_root, "app");
            _config = Path.Combine(_base, "Config");
            _src = Path.Combine(_root, "src");
            _vol = Path.Combine(_root, "vol");
            Directory.CreateDirectory(_config);
            Directory.CreateDirectory(_src);
            Assert.NotNull(new SevenZipClient(new SettingsService(_config, null)).SevenZipPath);

            // 必须是"压不动"的数据，否则 7z 会把几十 KB 全塞进第一卷，多片卷组造不出来
            for (int i = 1; i <= 12; i++)
            {
                var buf = new byte[3000];
                new Random(i * 7919).NextBytes(buf);
                File.WriteAllBytes(Path.Combine(_src, "f" + i + ".bin"), buf);
            }

            var (code, _, err) = await Run(_base, _config, "compress", _src, "--name", "z7.7z",
                "--type", "7z", "--volume", "16k", "-o", _vol, "--no-test");
            Assert.Equal(0, code);
            _set = Path.Combine(_vol, "z7.7z.001");
            Assert.True(File.Exists(_set));
            // 小分卷必须真的切成多片，否则"中间卷"用例就退化成解析单卷
            Assert.True(Directory.GetFiles(_vol, "z7.7z.0*").Length >= 3);
        }

        public Task DisposeAsync() { Dispose(); return Task.CompletedTask; }

        public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

        static async Task<(int code, List<string> o, List<string> e)> Run(string baseDir, string configDir, params string[] args)
        {
            var o = new List<string>(); var e = new List<string>();
            var runner = new CliRunner(baseDir, configDir, s => o.Add(s), s => e.Add(s));
            var code = await runner.RunAsync(CliParser.Parse(args));
            return (code, o, e);
        }

        string Vol(int n) => Path.Combine(_vol, "z7.7z." + n.ToString("000"));
        string NewDest() => Path.Combine(_root, "x_" + Guid.NewGuid().ToString("N"));

        // 把整组卷复制到独立目录，drop 里的卷号不复制（用来造缺主卷/缺中卷）
        string CopySet(string name, params int[] drop)
        {
            var dir = Path.Combine(_root, name);
            Directory.CreateDirectory(dir);
            foreach (var f in Directory.GetFiles(_vol, "z7.7z.0*"))
            {
                var baseName = Path.GetFileName(f);
                int n = int.Parse(baseName.Substring(baseName.Length - 3));
                if (drop.Contains(n)) continue;
                File.Copy(f, Path.Combine(dir, baseName));
            }
            return dir;
        }

        // ---------- 缺陷 1：中间卷要回落到主卷 ----------

        [Fact]
        public async Task List_of_middle_volume_falls_back_to_main_volume()
        {
            var (code, o, e) = await Run(_base, _config, "list", Vol(2));
            Assert.Equal(0, code);
            Assert.Empty(e);
            Assert.Contains(o, s => s.Contains("实际使用主卷"));
            // 清单内容来自主卷（12 个文件全在），不是中间卷的碎片
            Assert.Equal(12, o.Count(s => s.Contains("src/f") && s.Contains(".bin")));
            Assert.Contains(o, s => s.EndsWith(" 项"));
        }

        [Fact]
        public async Task Test_of_middle_volume_falls_back_to_main_volume()
        {
            var (code, o, e) = await Run(_base, _config, "test", Vol(3));
            Assert.Equal(0, code);
            Assert.Empty(e);
            Assert.Contains(o, s => s.EndsWith(": 正常"));
        }

        [Fact]
        public async Task Extract_of_middle_volume_falls_back_to_main_volume()
        {
            var dest = NewDest();
            var (code, o, e) = await Run(_base, _config, "extract", Vol(2), "-o", dest);
            Assert.Equal(0, code);
            Assert.Empty(e);
            Assert.Equal(12, Directory.GetFiles(dest, "*.bin", SearchOption.AllDirectories).Length);
        }

        // ---------- 缺陷 2：批量卷去重 ----------

        [Fact]
        public async Task Extract_of_all_volumes_extracts_once_and_reports_shadow_volumes()
        {
            var dest = NewDest();
            var all = Directory.GetFiles(_vol, "z7.7z.0*").OrderBy(s => s, StringComparer.Ordinal).ToArray();
            Assert.True(all.Length >= 3);

            var (code, o, e) = await Run(_base, _config, new[] { "extract" }.Concat(all).Concat(new[] { "-o", dest }).ToArray());
            Assert.Equal(0, code);
            Assert.Empty(e);
            Assert.Equal(12, Directory.GetFiles(dest, "*.bin", SearchOption.AllDirectories).Length);
            // 除主卷外每一卷都只报一句"已随分卷主文件处理"，不再各报一条错
            Assert.Equal(all.Length - 1, o.Count(s => s.Contains("已随分卷主文件处理")));
            // 成功行只能有一条，说明卷组只真解了一次
            Assert.Single(o.Where(s => s.Contains(" -> ")));
        }

        [Fact]
        public async Task Test_of_all_volumes_succeeds_overall()
        {
            var all = Directory.GetFiles(_vol, "z7.7z.0*").OrderBy(s => s, StringComparer.Ordinal).ToArray();
            var (code, o, e) = await Run(_base, _config, new[] { "test" }.Concat(all).ToArray());
            Assert.Equal(0, code);
            Assert.Empty(e);
            Assert.Contains(o, s => s.EndsWith(": 正常"));
        }

        // --json 是逐行 JSON 输出，从属卷的提示必须并进主卷对象的 volumes 字段，
        // 否则那行中文会把整条 NDJSON 流弄坏（下游 jq/CI 直接解析失败）。
        [Fact]
        public async Task List_json_batch_of_volumes_emits_one_object_with_volumes_field()
        {
            var all = Directory.GetFiles(_vol, "z7.7z.0*").OrderBy(s => s, StringComparer.Ordinal).ToArray();
            var (code, o, e) = await Run(_base, _config, new[] { "list" }.Concat(all).Concat(new[] { "--json" }).ToArray());
            Assert.Equal(0, code);
            Assert.Empty(e);
            var line = Assert.Single(o);                       // 整组卷只产出一个 JSON 对象
            using var doc = System.Text.Json.JsonDocument.Parse(line);
            Assert.Equal(Path.GetFileName(Vol(1)), Path.GetFileName(doc.RootElement.GetProperty("archive").GetString()));
            Assert.Equal(all.Length - 1, doc.RootElement.GetProperty("volumes").GetArrayLength());
        }

        // 用户把中间卷写在主卷前面时，主卷仍要先处理，卷内顺序不该决定谁"随"谁
        [Fact]
        public async Task Extract_handles_main_volume_first_even_if_given_last()
        {
            var dest = NewDest();
            var (code, o, e) = await Run(_base, _config, "extract", Vol(3), Vol(1), Vol(2), "-o", dest);
            Assert.Equal(0, code);
            Assert.Empty(e);
            var success = Assert.Single(o.Where(s => s.Contains(" -> ")));
            Assert.Contains(Path.GetFileName(Vol(1)), success);
        }

        // ---------- 缺陷 3 / 4：缺卷诊断 ----------

        [Fact]
        public async Task List_reports_which_middle_volume_is_missing()
        {
            var dir = CopySet("gap_mid", 2);
            var main = Path.Combine(dir, "z7.7z.001");
            var (code, _, e) = await Run(_base, _config, "list", main);
            Assert.Equal(1, code);
            var msg = Assert.Single(e);
            Assert.Contains("分卷不完整", msg);
            Assert.Contains("z7.7z.002", msg);          // 缺的是哪一卷要点名
        }

        [Fact]
        public async Task Extract_reports_which_middle_volume_is_missing_without_extracting()
        {
            var dir = CopySet("gap_mid_x", 2);
            var dest = NewDest();
            var (code, _, e) = await Run(_base, _config, "extract", Path.Combine(dir, "z7.7z.001"), "-o", dest);
            Assert.Equal(1, code);
            Assert.Contains("z7.7z.002", Assert.Single(e));
            // 关键的"别把缺口当坏包"：一卷都不该解出来
            Assert.Empty(Directory.Exists(dest) ? Directory.GetFiles(dest, "*", SearchOption.AllDirectories) : Array.Empty<string>());
        }

        [Fact]
        public async Task Only_middle_volumes_without_main_volume_says_main_is_missing()
        {
            var dir = CopySet("no_main", 1);
            var (code, _, e) = await Run(_base, _config, "list", Path.Combine(dir, "z7.7z.002"));
            Assert.Equal(1, code);
            var msg = Assert.Single(e);
            Assert.Contains("缺少主卷", msg);
            Assert.Contains("z7.7z.001", msg);
        }

        [Fact]
        public async Task Test_of_middle_volume_without_main_volume_fails()
        {
            var dir = CopySet("no_main_t", 1);
            var (code, o, e) = await Run(_base, _config, "test", Path.Combine(dir, "z7.7z.003"));
            Assert.Equal(1, code);
            Assert.Contains("缺少主卷", Assert.Single(e));
            Assert.DoesNotContain(o, s => s.EndsWith(": 正常"));   // 不能报"正常"
        }

        // 末卷缺失：7z 只会说"不是压缩包"，CLI 要说出真正原因
        [Fact]
        public async Task Missing_last_volume_is_reported_as_incomplete_set()
        {
            var dir = Path.Combine(_root, "no_tail");
            Directory.CreateDirectory(dir);
            var vols = Directory.GetFiles(_vol, "z7.7z.0*").OrderBy(s => s, StringComparer.Ordinal).ToArray();
            foreach (var f in vols.Take(vols.Length - 1))
                File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
            var (code, _, e) = await Run(_base, _config, "list", Path.Combine(dir, "z7.7z.001"));
            Assert.Equal(1, code);
            var msg = Assert.Single(e);
            Assert.Contains("分卷不完整", msg);
            Assert.Contains("末卷", msg);
        }

        // ---------- 缺陷 6：分卷 zip 的中间卷不能被当成独立 zip ----------

        [Fact]
        public async Task Middle_volume_of_zip_set_is_not_treated_as_standalone_archive()
        {
            string zdir = Path.Combine(_root, "zipset");
            var (cc, _, _) = await Run(_base, _config, "compress", _src, "--name", "zz.zip",
                "--type", "zip", "--volume", "16k", "-o", zdir, "--no-test");
            Assert.Equal(0, cc);
            var volumes = Directory.GetFiles(zdir, "zz.zip.0*").OrderBy(s => s, StringComparer.Ordinal).ToArray();
            Assert.True(volumes.Length >= 3);

            // 序号 ≥2 的卷本身是能"解出点东西"的裸 zip 片段；以前会被当独立包解出半截
            var middle = volumes[1];
            var dest = NewDest();
            var (code, _, e) = await Run(_base, _config, "extract", middle, "-o", dest);
            Assert.Equal(0, code);
            Assert.Empty(e);
            Assert.Equal(12, Directory.GetFiles(dest, "*.bin", SearchOption.AllDirectories).Length);
        }

        // 非压缩包不能被分卷逻辑吞掉：错误文案与退出码维持原样
        [Fact]
        public async Task Plain_text_file_still_reports_unsupported_format()
        {
            var txt = Path.Combine(_root, "readme.txt");
            File.WriteAllText(txt, "hello");
            var (code, _, e) = await Run(_base, _config, "list", txt);
            Assert.Equal(1, code);
            var msg = Assert.Single(e);
            Assert.DoesNotContain("分卷", msg);
        }
    }
}
