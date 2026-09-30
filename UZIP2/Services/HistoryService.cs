using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using UZIP2.Models;

namespace UZIP2.Services
{
    public interface IHistoryService
    {
        IReadOnlyList<HistoryEntry> ReadAll();          // 最新在前
        void Record(JobEntry job);                       // 收尾时落一条（只记 Success/Failed）
        void Clear();
    }

    // 持久化的统一作业历史，落 Config\history.json。参照 SettingsService 的 JSON 风格，
    // 但额外用 RelaxedJsonEscaping 让中文可读。密码只在 settings.LogPasswords 打开时记录，
    // 关闭则留 null——与 CompressLogService 的脱敏门控保持一致。
    public sealed class HistoryService : IHistoryService
    {
        public const int MaxEntries = 500;

        readonly string _configDir;
        readonly ISettingsService _settings;
        readonly object _sync = new object();
        List<HistoryEntry> _cache;   //  oldest -> newest

        static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public HistoryService(string configDirectory, ISettingsService settings = null)
        {
            _configDir = configDirectory;
            _settings = settings;
        }

        public string HistoryPath => Path.Combine(_configDir, "history.json");

        public IReadOnlyList<HistoryEntry> ReadAll()
        {
            lock (_sync)
            {
                var list = LoadLocked();
                var copy = list.ToList();
                copy.Reverse();               // newest first
                return copy;
            }
        }

        public void Record(JobEntry job)
        {
            if (job == null) return;
            if (job.Status != JobStatus.Success && job.Status != JobStatus.Failed) return;

            var entry = BuildEntry(job, _settings?.Current?.LogPasswords ?? true);
            lock (_sync)
            {
                var list = LoadLocked();
                list.Add(entry);
                if (list.Count > MaxEntries)
                    list.RemoveRange(0, list.Count - MaxEntries);   // 丢最旧
                _cache = list;
                PersistLocked(list);
            }
        }

        public void Clear()
        {
            lock (_sync)
            {
                _cache = new List<HistoryEntry>();
                try { if (File.Exists(HistoryPath)) File.Delete(HistoryPath); } catch { }
            }
        }

        // 纯映射，便于单测：不读文件、不写文件、不依赖实例状态。
        public static HistoryEntry BuildEntry(JobEntry job, bool keepPassword)
        {
            bool ok = job.Status == JobStatus.Success;
            long dur = 0;
            if (job.StartedUtc != default)
            {
                var d = DateTime.UtcNow - job.StartedUtc;
                if (d.Ticks > 0) dur = (long)d.TotalMilliseconds;
            }
            string password = null;
            if (keepPassword && ok)
                password = string.IsNullOrEmpty(job.UsedPassword) ? null : job.UsedPassword;

            return new HistoryEntry
            {
                Kind = job.Kind,
                Source = job.Archive,
                Destination = job.OutputDir,
                Status = ok ? "Success" : "Failed",
                Error = ok ? null : job.Diagnosis,
                Advice = ok ? null : HistoryAdvice.For(job.Kind, job.Diagnosis),
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Count = job.Done,
                DurationMs = dur,
                Password = password
            };
        }

        // 调用方需持锁
        List<HistoryEntry> LoadLocked()
        {
            if (_cache != null) return _cache;
            var list = new List<HistoryEntry>();
            try
            {
                if (File.Exists(HistoryPath))
                {
                    var json = File.ReadAllText(HistoryPath);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        var parsed = JsonSerializer.Deserialize<List<HistoryEntry>>(json, JsonOptions);
                        if (parsed != null) list = parsed;
                    }
                }
            }
            catch
            {
                // 文件损坏：备份后从空开始，绝不因历史坏掉而拖垮主流程
                try
                {
                    var bad = HistoryPath + ".bad-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                    File.Move(HistoryPath, bad, true);
                }
                catch { }
                list = new List<HistoryEntry>();
            }
            _cache = list;
            return list;
        }

        // 调用方需持锁
        void PersistLocked(List<HistoryEntry> list)
        {
            try
            {
                Directory.CreateDirectory(_configDir);
                AtomicFile.Write(HistoryPath, JsonSerializer.Serialize(list, JsonOptions));
            }
            catch { }   // 历史写失败不影响解压/压缩本身
        }
    }
}
