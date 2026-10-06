using System;
using System.IO;
using System.Threading.Tasks;
using UZIP2.Models;
using UZIP2.Services;
using UZIP2.ViewModel;
using Xunit;

namespace UZIP2.Tests
{
    public class CompressPresetResolverTests : IDisposable
    {
        readonly string _root = Path.Combine(Path.GetTempPath(), "UZipPresetRule_" + Guid.NewGuid().ToString("N"));
        public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

        static CompressPreset Preset(string folder, int type = -1, int level = -1, string volume = "", string encrypt = "")
            => new CompressPreset { Name = folder, Folder = folder, CompressType = type, CompressLevel = level, CompressVolume = volume, EncryptHeaders = encrypt };

        [Fact]
        public void No_preset_falls_back_to_global()
        {
            var g = new AppSettings { CompressType = 0, CompressLevel = 5, CompressVolume = "700m", HideZipContent = true };
            Assert.Null(CompressPresetResolver.Find(_root, null));
            Assert.Null(CompressPresetResolver.Find(_root, new CompressPreset[0]));
            var plan = CompressPresetResolver.Plan(g, CompressPresetResolver.Find(_root, new CompressPreset[0]));
            Assert.Equal(0, plan.Type);
            Assert.Equal(5, plan.Level);
            Assert.Equal("700m", plan.Volume);
            Assert.True(plan.EncryptHeaders);
            Assert.Null(plan.PresetName);
        }

        [Fact]
        public void Deep_preset_beats_shallow_preset()
        {
            string deep = Path.Combine(_root, "a");
            string deeper = Path.Combine(deep, "inbox");
            Directory.CreateDirectory(deeper);

            var presets = new[] { Preset(deep, type: 1), Preset(deeper, type: 3) };
            var hit = CompressPresetResolver.Find(Path.Combine(deeper, "file.bin"), presets);
            Assert.Equal(deeper, hit.Folder);

            // 源本身是目录时也按它自己匹配，而不是父目录
            Assert.Equal(deeper, CompressPresetResolver.Find(deeper, presets).Folder);

            // 只在预设目录之下的路径才会命中
            Assert.Null(CompressPresetResolver.Find(_root, presets));
        }

        [Fact]
        public void Matching_ignores_case_and_trailing_slash()
        {
            string dir = Path.Combine(_root, "case_dir");
            Directory.CreateDirectory(dir);

            var presets = new[] { Preset(dir + "\\", type: 1) };
            var hit = CompressPresetResolver.Find(Path.Combine(dir.ToUpperInvariant(), "sub", "x.bin"), presets);
            Assert.NotNull(hit);
            Assert.Equal(1, hit.CompressType);
        }

        [Fact]
        public void Blank_folder_never_matches()
        {
            Directory.CreateDirectory(_root);
            var hit = CompressPresetResolver.Find(_root, new[] { Preset("  ", type: 1) });
            Assert.Null(hit);
        }

        [Fact]
        public void Preset_fills_only_the_fields_it_sets()
        {
            var g = new AppSettings { CompressType = 0, CompressLevel = 9, CompressVolume = "1g", HideZipContent = true };
            var plan = CompressPresetResolver.Plan(g, Preset("C:\\nope", type: 1, volume: "5m"));
            Assert.Equal(1, plan.Type);
            Assert.Equal(9, plan.Level);              // 未设置 → 全局
            Assert.Equal("5m", plan.Volume);
            Assert.True(plan.EncryptHeaders);         // 未设置 → 全局
        }

        [Fact]
        public void Preset_can_turn_global_options_off()
        {
            var g = new AppSettings { CompressVolume = "1g", HideZipContent = true };
            var plan = CompressPresetResolver.Plan(g, Preset("C:\\nope", level: 0, encrypt: "off"));
            Assert.False(plan.EncryptHeaders);
            Assert.Equal("1g", plan.Volume);          // 分卷只能覆盖，不能关
            Assert.Equal(0, plan.Level);
        }
    }

    public class CompressPresetRowTests
    {
        [Theory]
        [InlineData(0, -1)]
        [InlineData(1, 0)]    // zip
        [InlineData(2, 1)]    // 7z
        [InlineData(8, 7)]    // 超出范围也不会崩，原样写回
        public void Type_index_maps_with_global_offset(int index, int expected)
        {
            var row = new SettingsViewModel.PresetRow(new CompressPreset()) { TypeIndex = index };
            Assert.Equal(expected, row.ToModel().CompressType);
        }

        [Theory]
        [InlineData(0, -1)]
        [InlineData(1, 0)]
        [InlineData(4, 5)]
        [InlineData(6, 9)]
        public void Level_index_maps_onto_the_global_level_ladder(int index, int expected)
        {
            var row = new SettingsViewModel.PresetRow(new CompressPreset()) { LevelIndex = index };
            Assert.Equal(expected, row.ToModel().CompressLevel);
        }

        [Theory]
        [InlineData(0, "")]
        [InlineData(1, "off")]
        [InlineData(2, "on")]
        public void Encrypt_index_maps_onto_switch_text(int index, string expected)
        {
            var row = new SettingsViewModel.PresetRow(new CompressPreset()) { EncryptIndex = index };
            Assert.Equal(expected, row.ToModel().EncryptHeaders);
        }

        [Fact]
        public void Row_round_trips_a_saved_preset()
        {
            var p = new CompressPreset
            {
                Name = "下载", Folder = @"D:\Downloads", CompressType = 1,
                CompressLevel = 9, CompressVolume = "700m", EncryptHeaders = "on"
            };
            var row = new SettingsViewModel.PresetRow(p);
            var back = row.ToModel();
            Assert.Equal(p.Name, back.Name);
            Assert.Equal(p.Folder, back.Folder);
            Assert.Equal(p.CompressType, back.CompressType);
            Assert.Equal(p.CompressLevel, back.CompressLevel);
            Assert.Equal(p.CompressVolume, back.CompressVolume);
            Assert.Equal(p.EncryptHeaders, back.EncryptHeaders);
        }

        [Fact]
        public void Unknown_level_snaps_to_the_nearest_listed_one()
        {
            // 老配置或手改过的 settings.json 里可能出现下拉没有的级别
            var row = new SettingsViewModel.PresetRow(new CompressPreset { CompressLevel = 4 });
            Assert.Equal(4, row.LevelIndex);
            Assert.Equal(5, row.ToModel().CompressLevel);
        }

        [Fact]
        public void Null_preset_reads_as_all_follow_global()
        {
            var row = new SettingsViewModel.PresetRow(null);
            Assert.Equal(0, row.TypeIndex);
            Assert.Equal(0, row.LevelIndex);
            Assert.Equal(0, row.EncryptIndex);
            Assert.Equal("", row.Folder);
        }
    }

    public class CompressPresetWorkerTests : IDisposable
    {
        readonly string _root, _presetDir, _outside, _out;
        readonly SettingsService _settings;
        readonly SevenZipClient _client;
        readonly ArchiveWorker _worker;

        public CompressPresetWorkerTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "UZipPresetTests_" + Guid.NewGuid().ToString("N"));
            _presetDir = Path.Combine(_root, "movies");
            _outside = Path.Combine(_root, "other");
            _out = Path.Combine(_root, "out");
            Directory.CreateDirectory(Path.Combine(_presetDir, "sub"));
            Directory.CreateDirectory(_outside);
            Directory.CreateDirectory(_out);
            _settings = new SettingsService(Path.Combine(_root, "Config"), null);
            _settings.Current.CompressOutMode = 1;   // 输出落在源文件目录，方便断言
            var passwords = new PasswordService(Path.Combine(_root, "Config"), _settings);
            _client = new SevenZipClient(_settings);
            Assert.NotNull(_client.SevenZipPath);
            _worker = new ArchiveWorker(_client, passwords, _settings);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        string MakeFile(string dir, string name)
        {
            var buf = new byte[64 * 1024];
            new Random(11).NextBytes(buf);
            var p = Path.Combine(dir, name);
            File.WriteAllBytes(p, buf);
            return p;
        }

        [Fact]
        public async Task Preset_overrides_type_and_volume_for_its_folder()
        {
            var inside = MakeFile(Path.Combine(_presetDir, "sub"), "clip.bin");
            var outside = MakeFile(_outside, "clip.bin");

            _settings.Current.CompressType = 0;                    // 全局 zip
            _settings.Current.CompressPresets.Add(new CompressPreset
            {
                Name = "视频", Folder = _presetDir, CompressType = 1, CompressVolume = "40k"
            });

            _worker.EnqueueCompress(new[] { inside });
            await _worker.WhenIdleAsync();
            var job = Assert.Single(_worker.Jobs);
            Assert.Equal(JobStatus.Success, job.Status);
            Assert.EndsWith("clip.7z.001", job.OutputDir);         // 预设：7z + 分卷
            Assert.True(File.Exists(job.OutputDir));
            Assert.True(File.Exists(Path.Combine(_presetDir, "sub", "clip.7z.002")));

            _worker.EnqueueCompress(new[] { outside });
            await _worker.WhenIdleAsync();
            var plain = _worker.Jobs[_worker.Jobs.Count - 1];
            Assert.Equal(JobStatus.Success, plain.Status);
            Assert.EndsWith("clip.zip", plain.OutputDir);          // 目录外：全局 zip 单文件
            Assert.False(File.Exists(plain.OutputDir + ".001"));
        }

        [Fact]
        public async Task Preset_volume_is_validated_before_running_7z()
        {
            var f = MakeFile(_presetDir, "clip.bin");
            _settings.Current.CompressPresets.Add(new CompressPreset { Folder = _presetDir, CompressVolume = "700mb" });

            _worker.EnqueueCompress(new[] { f });
            await _worker.WhenIdleAsync();

            var job = Assert.Single(_worker.Jobs);
            Assert.Equal(JobStatus.Failed, job.Status);
            Assert.Contains("分卷大小格式不正确", job.Diagnosis);
            Assert.Empty(Directory.GetFiles(_presetDir, "clip.zip*"));
        }

        [Fact]
        public void Settings_written_by_preset_survive_a_reload()
        {
            _settings.Current.CompressPresets.Add(new CompressPreset
            {
                Name = "视频", Folder = _presetDir, CompressType = 1, CompressLevel = 7,
                CompressVolume = "700m", EncryptHeaders = "on"
            });
            _settings.Save(_ => { });

            var reopened = new SettingsService(Path.Combine(_root, "Config"), null);
            var p = Assert.Single(reopened.Current.CompressPresets);
            Assert.Equal("视频", p.Name);
            Assert.Equal(_presetDir, p.Folder);
            Assert.Equal(1, p.CompressType);
            Assert.Equal(7, p.CompressLevel);
            Assert.Equal("700m", p.CompressVolume);
            Assert.Equal("on", p.EncryptHeaders);
        }
    }

    public class CompressPresetPersistTests : IDisposable
    {
        readonly string _root;
        readonly SettingsService _settings;
        readonly SettingsViewModel _vm;

        public CompressPresetPersistTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "UZipPresetVm_" + Guid.NewGuid().ToString("N"));
            _settings = new SettingsService(_root, null);
            _vm = new SettingsViewModel(_settings);
        }

        public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

        [Fact]
        public void Rows_without_a_folder_are_not_saved()
        {
            _vm.AddPreset();                      // 名称有、目录空
            _vm.AddPreset();
            _vm.Presets[1].Folder = @"D:\Downloads";
            _vm.Presets[1].TypeIndex = 2;

            var saved = new SettingsService(_root, null).Current;
            var p = Assert.Single(saved.CompressPresets);
            Assert.Equal(@"D:\Downloads", p.Folder);
            Assert.Equal(1, p.CompressType);
            Assert.Equal(-1, p.CompressLevel);
        }

        [Fact]
        public void Saved_rows_come_back_as_rows()
        {
            _vm.AddPreset();
            _vm.Presets[0].Folder = @"E:\Backup";
            _vm.Presets[0].Volume = "700m";
            _vm.Presets[0].EncryptIndex = 2;

            var reloaded = new SettingsViewModel(new SettingsService(_root, null));
            var row = Assert.Single(reloaded.Presets);
            Assert.Equal(@"E:\Backup", row.Folder);
            Assert.Equal("700m", row.Volume);
            Assert.Equal(2, row.EncryptIndex);

            reloaded.RemovePreset(row);
            Assert.Empty(reloaded.Presets);
            Assert.Empty(new SettingsService(_root, null).Current.CompressPresets);
        }
    }
}
