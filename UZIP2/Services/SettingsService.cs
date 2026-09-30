using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using UZIP2.Models;

namespace UZIP2.Services
{
    public interface ISettingsService
    {
        AppSettings Current { get; }
        void Save(Action<AppSettings> mutate);
        event Action<AppSettings> Changed;
        string SettingsPath { get; }
        string ConfigDirectory { get; }
    }

    // settings.json 读写。原子写：先写 temp 再 File.Move 替换。
    public sealed class SettingsService : ISettingsService
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private readonly object _sync = new object();
        private readonly IFileLogger _logger;
        private AppSettings _current;

        public string ConfigDirectory { get; }
        public string SettingsPath => Path.Combine(ConfigDirectory, "settings.json");

        public AppSettings Current
        {
            get
            {
                lock (_sync)
                {
                    if (_current == null)
                        _current = Load();
                    return _current;
                }
            }
        }

        public event Action<AppSettings> Changed;

        public SettingsService(string configDirectory, IFileLogger logger)
        {
            ConfigDirectory = configDirectory;
            _logger = logger;
        }

        private AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions);
                    if (loaded != null)
                        return loaded;
                }
            }
            catch (Exception ex)
            {
                // 损坏时备份再重建，避免用户配置直接丢失
                try
                {
                    var corrupt = SettingsPath + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                    File.Move(SettingsPath, corrupt);
                    _logger?.Error($"settings.json 解析失败，已备份为 {Path.GetFileName(corrupt)}", ex);
                }
                catch
                {
                    _logger?.Error("settings.json 解析失败且无法备份", ex);
                }
            }

            var fresh = new AppSettings();
            Write(fresh);
            return fresh;
        }

        public void Save(Action<AppSettings> mutate)
        {
            lock (_sync)
            {
                var settings = Current;
                mutate?.Invoke(settings);
                Write(settings);
            }
            Changed?.Invoke(_current);
        }

        // 迁移器直接落盘一份完整设置
        public void ReplaceAll(AppSettings settings)
        {
            lock (_sync)
            {
                _current = settings;
                Write(settings);
            }
            Changed?.Invoke(settings);
        }

        private void Write(AppSettings settings)
        {
            Directory.CreateDirectory(ConfigDirectory);
            var temp = SettingsPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temp, SettingsPath, true);
        }
    }
}
