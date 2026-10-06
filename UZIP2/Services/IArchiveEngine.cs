using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UZIP2.Models;

namespace UZIP2.Services
{
    // worker 眼里的压缩包引擎。抽出来只为一个目的：单测能在不碰真实 7z 语义的前提下
    // 数清楚一个作业到底起了几遍全盘读（Test/Extract 次数就是耗时账本）。
    public interface IArchiveEngine
    {
        Task<SevenZipResult> TestAsync(string archive, string password, CancellationToken ct);

        Task<EncryptionState> ProbeEncryptionAsync(string archive, CancellationToken ct);

        Task<SevenZipResult> ExtractAsync(string archive, string dest, string password,
            IProgress<SevenZipProgress> progress, CancellationToken ct, string coverMode = "-aos",
            IReadOnlyList<string> onlyEntries = null);

        Task<ArchiveListing> ListEntriesAsync(string archive, string password, CancellationToken ct);

        Task<SevenZipResult> CompressAsync(IReadOnlyList<string> files, string outArchive, string password,
            int compressType, int level, bool hideContent, IProgress<SevenZipProgress> progress, CancellationToken ct,
            IReadOnlyList<string> excludeFilters = null, string volumeSize = null,
            string solid = null, string threads = null);
    }
}
