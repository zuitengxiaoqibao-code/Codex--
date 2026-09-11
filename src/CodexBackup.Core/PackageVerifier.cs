using System.Text.RegularExpressions;

namespace CodexBackup.Core;
public sealed class PackageVerifier
{
    public async Task<VerifiedPackage> VerifyAsync(string packagePath, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var ct = cancellationToken;
        ct.ThrowIfCancellationRequested();
        var path = PathSafety.Full(packagePath);
        PathSafety.RejectReparseAncestors(path);
        using var packageLease = DirectoryLease.Acquire(path);
        var marker = FileIO.ReadJson<CompletionMarker>(Path.Combine(path, "COMPLETE.json"), 4096);
        using var manifestPin = FileIO.OpenRead(Path.Combine(path, "manifest.json"));
        using var inventoryPin = FileIO.OpenRead(Path.Combine(path, "inventory.sqlite"));
        if (marker.FormatVersion != 1) throw new BackupException("不支持此备份版本。");
        if (!string.Equals(marker.ManifestSha256, await FileIO.HashAsync(Path.Combine(path, "manifest.json"), ct), StringComparison.Ordinal) ||
            !string.Equals(marker.InventorySha256, await FileIO.HashAsync(Path.Combine(path, "inventory.sqlite"), ct), StringComparison.Ordinal))
            throw new BackupException("备份清单摘要不匹配，可能损坏或被更改。");
        var manifest = FileIO.ReadJson<BackupManifest>(Path.Combine(path, "manifest.json"));
        if (manifest.FormatVersion != 1 || manifest.BackupId != marker.BackupId) throw new BackupException("备份版本或身份不匹配。");
        List<FileRecord> records;
        try { records = InventoryStore.Read(Path.Combine(path, "inventory.sqlite"), ct); }
        catch (Microsoft.Data.Sqlite.SqliteException) { throw new BackupException("备份文件数据库损坏或结构不受支持。"); }
        await ValidateAsync(path, manifest, records, progress, ct);
        return new(path, manifest, records);
    }

    internal static async Task ValidateAsync(string path, BackupManifest manifest, IReadOnlyList<FileRecord> records, IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        if (manifest.Roots is null || manifest.Roots.Count is < 1 or > 10000 || manifest.FileCount < 0 || manifest.TotalBytes < 0)
            throw new BackupException("根目录清单或统计值无效。");
        var roots = new Dictionary<string, BackupRoot>(StringComparer.Ordinal);
        foreach (var root in manifest.Roots)
        {
            if (!IsId(root.Id) || !roots.TryAdd(root.Id, root) || !Enum.IsDefined(root.Kind) || root.Name.Length > 32768)
                throw new BackupException("备份根目录 ID 重复或无效。");
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var paths = new Dictionary<string, FileRecord>(StringComparer.OrdinalIgnoreCase);
        long bytes = 0, count = 0;
        foreach (var f in records)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsId(f.Id) || !ids.Add(f.Id) || !roots.ContainsKey(f.RootId) || f.Length < 0 || f.LastWriteUtcTicks < DateTime.MinValue.Ticks || f.LastWriteUtcTicks > DateTime.MaxValue.Ticks)
                throw new BackupException("文件清单 ID、大小、时间或根目录无效。");
            if (f.RelativePath.Length != 0) PathSafety.ValidateRelative(f.RelativePath);
            if (!paths.TryAdd(f.RootId + "|" + f.RelativePath.Replace('/', '\\'), f)) throw new BackupException("文件清单存在重复或大小写冲突路径。");
            if (f.RelativePath.Length == 0 && f.IsDirectory != roots[f.RootId].IsDirectory) throw new BackupException("根条目类型不一致。");
            if (f.IsDirectory)
            {
                if (f.Length != 0 || f.Sha256.Length != 0) throw new BackupException("目录清单包含无效载荷。");
                continue;
            }
            if (!Regex.IsMatch(f.Sha256, "^[0-9A-F]{64}$", RegexOptions.CultureInvariant)) throw new BackupException("无效的文件校验值。");
            var payload = PathSafety.Under(Path.Combine(path, "payload"), f.Id);
            if (!File.Exists(payload) || new FileInfo(payload).Length != f.Length || await FileIO.HashAsync(payload, ct) != f.Sha256)
                throw new BackupException($"载荷缺失或校验失败：{f.Id}");
            try { bytes = checked(bytes + f.Length); } catch (OverflowException) { throw new BackupException("载荷大小超出上限。"); }
            count++;
            progress?.Report(new("完整校验", $"已验证 {count:N0} 个文件", count, bytes, manifest.TotalBytes));
        }
        foreach (var root in manifest.Roots)
            if (!paths.ContainsKey(root.Id + "|")) throw new BackupException("缺少根条目。");
        foreach (var f in records.Where(f => f.RelativePath.Length != 0))
        {
            var parent = Path.GetDirectoryName(f.RelativePath.Replace('/', '\\')) ?? "";
            if (!paths.TryGetValue(f.RootId + "|" + parent, out var parentEntry) || !parentEntry.IsDirectory)
                throw new BackupException("条目的父目录缺失或被文件占用。");
        }
        if (count != manifest.FileCount || bytes != manifest.TotalBytes) throw new BackupException("备份统计与清单不一致。");
    }

    private static bool IsId(string id) => id is not null && Regex.IsMatch(id, "^[0-9a-f]{32}$", RegexOptions.CultureInvariant);
}
