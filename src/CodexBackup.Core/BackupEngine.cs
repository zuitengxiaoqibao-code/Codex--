using System.Text;
using System.Net;

namespace CodexBackup.Core;

public sealed class BackupEngine
{
    public async Task<BackupResult> BackupAsync(BackupRequest request, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var ct = cancellationToken;
        ct.ThrowIfCancellationRequested();
        var gaps = MigrationCoverage.Evaluate(request);
        if (gaps.Count > 0) throw new BackupException("还不能制作完整迁移包：\n" + string.Join("\n", gaps.Take(12).Select(f => f.Message + " " + f.Path)) + (gaps.Count > 12 ? $"\n另有 {gaps.Count - 12} 项，请查看完整性检查列表。" : ""));
        var selected = ValidateSelection(request.Sources);
        WriterGuard.RequireStoppedForSources(selected);
        var target = PathSafety.Full(request.DestinationDirectory);
        PathSafety.RejectReparseAncestors(target);
        PathSafety.RequireNtfs(target);
        foreach (var s in selected)
        {
            if (PathSafety.Contains(s.Path, target) || PathSafety.Contains(target, s.Path))
                throw new BackupException("备份位置不能与来源目录相互包含。请选择独立目录。");
            if (Environment.ProcessPath is { } exe && PathSafety.Contains(s.Path, exe))
                throw new BackupException("本工具正在所选来源目录内运行，请把 EXE 移到独立目录后重试。");
        }
        var manifest = new BackupManifest { Roots = NormalizeRoots(selected), CoverageNotes = request.CoverageNotes.ToList(), ToolVersion = ProductInfo.Version,
            CompleteMigration = request.CompleteMigration, Sessions = request.Sessions.ToList(), LogicalSources = request.Sources.ToList(), PathReplacements = new(request.PathReplacements, StringComparer.OrdinalIgnoreCase),
            Exclusions = request.Sources.Where(s => !s.Selected).Select(s => $"未作为独立项目选择（父目录可能包含）：{s.Name} | {s.Path}").ToList(),
            SourceCodexVersion = string.IsNullOrWhiteSpace(request.SourceCodexVersion) ? "未知" : request.SourceCodexVersion,
            EnvironmentManifest = request.EnvironmentManifest ?? new(), Preflight = request.Preflight };
        manifest.CoverageNotes.Add("范围仅限所选来源及已发现依赖；未挂载磁盘、外部服务、系统凭据和未知位置未由此备份保证覆盖。");
        manifest.CoverageNotes.Add("未使用密码加密；备份包含敏感原始配置。硬链接按独立内容副本保存，不保留共享 inode 关系。");
        progress?.Report(new("预检", "枚举文件并检查类型、权限与目标空间"));
        var files = Snapshot(manifest.Roots, manifest.Exclusions, ct);
        MigrationCoverage.ValidateInventory(manifest, files);
        manifest.CoverageNotes.Insert(0, manifest.CompleteMigration ? "已核对本次发现的会话、对应源码及必需文件均在清单中；运行环境及应用可用性需恢复后检查。" : "这是自选/抢救备份，不是完整迁移包。未选择、缺失或未发现的项目源码可能无法恢复。");
        manifest.TotalBytes = files.Sum(f => f.Length);
        manifest.FileCount = files.Count(f => !f.IsDirectory);
        var free = new DriveInfo(Path.GetPathRoot(target)!).AvailableFreeSpace;
        if (free < checked(manifest.TotalBytes + Math.Max(64L * 1024 * 1024, manifest.TotalBytes / 20)))
            throw new BackupException("目标空间不足，需要载荷大小及至少 64 MiB / 5% 的额外余量。");
        Directory.CreateDirectory(target);
        var name = $"CodexBackup-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{manifest.BackupId}";
        var staging = Path.Combine(target, name + ".incomplete");
        Directory.CreateDirectory(Path.Combine(staging, "payload"));
        var byRoot = manifest.Roots.ToDictionary(r => r.Id);
        long copied = 0, count = 0;
        var nextWriterCheck = DateTime.UtcNow;
        foreach (var file in files.Where(f => !f.IsDirectory))
        {
            ct.ThrowIfCancellationRequested();
            if (DateTime.UtcNow >= nextWriterCheck)
            { WriterGuard.RequireStoppedForSources(selected); nextWriterCheck = DateTime.UtcNow.AddSeconds(1); }
            var source = SourcePath(byRoot[file.RootId], file);
            FileIO.ValidateSourceType(source);
            file.Sha256 = await FileIO.HashAsync(source, ct);
            await FileIO.CopyVerifiedAsync(source, Path.Combine(staging, "payload", file.Id), file.Sha256, ct);
            copied += file.Length;
            count++;
            progress?.Report(new("备份", $"已复制并校验 {count:N0} 个文件", count, copied, manifest.TotalBytes));
        }
        progress?.Report(new("复核来源", "再次读取来源，检查备份期间是否有文件变化", count, copied, manifest.TotalBytes));
        await ValidateSnapshotAsync(manifest.Roots, files, ct);
        WriterGuard.RequireStoppedForSources(selected);
        InventoryStore.Write(Path.Combine(staging, "inventory.sqlite"), files);
        FileIO.WriteJsonDurable(Path.Combine(staging, "manifest.json"), manifest);
        WriteReports(staging, manifest);
        var marker = new CompletionMarker { BackupId = manifest.BackupId,
            ManifestSha256 = await FileIO.HashAsync(Path.Combine(staging, "manifest.json"), ct),
            InventorySha256 = await FileIO.HashAsync(Path.Combine(staging, "inventory.sqlite"), ct) };
        ct.ThrowIfCancellationRequested();
        // Validate the material before publishing a completion marker.
        await PackageVerifier.ValidateAsync(staging, manifest, files, progress, ct);
        ct.ThrowIfCancellationRequested();
        FileIO.WriteJsonDurable(Path.Combine(staging, "COMPLETE.json"), marker);
        var final = Path.Combine(target, name);
        Directory.Move(staging, final);
        progress?.Report(new("完成", "所选范围文件校验通过；请查看未覆盖范围与报告", count, copied, manifest.TotalBytes));
        return new(final, manifest);
    }

    internal static List<SourceItem> ValidateSelection(List<SourceItem> sources)
    {
        if (sources.Any(s => s.Required && !s.Selected)) throw new BackupException("必需项不能取消选择。");
        if (sources.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != sources.Count) throw new BackupException("来源 ID 重复。");
        var selected = sources.Where(s => s.Selected).ToList();
        if (selected.Count == 0) throw new BackupException("至少选择一个备份来源。");
        var ids = selected.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var s in selected)
        {
            if (s.DependencyIds.Any(id => !ids.Contains(id))) throw new BackupException($"{s.Name} 的必需依赖未选中或未找到。");
            s.Path = PathSafety.Full(s.Path);
            PathSafety.RejectReparseAncestors(s.Path);
            if (!File.Exists(s.Path) && !Directory.Exists(s.Path)) throw new BackupException($"所选来源不存在或无权限：{s.Path}");
            s.IsDirectory = Directory.Exists(s.Path);
        }
        return selected;
    }

    internal static List<BackupRoot> NormalizeRoots(List<SourceItem> selected)
    {
        var roots = new List<BackupRoot>();
        foreach (var s in selected.OrderBy(s => s.Path.Length))
        {
            var parent = roots.FirstOrDefault(r => (r.IsDirectory && PathSafety.Contains(r.OriginalPath, s.Path)) || r.OriginalPath.Equals(s.Path, StringComparison.OrdinalIgnoreCase));
            if (parent is not null)
            {
                parent.SourceIds.Add(s.Id);
                if (s.Kind is SourceKind.Core or SourceKind.Application or SourceKind.Tool) parent.Kind = s.Kind;
            }
            else roots.Add(new BackupRoot { Id = Guid.NewGuid().ToString("N"), Name = s.Name, OriginalPath = s.Path, IsDirectory = s.IsDirectory, Kind = s.Kind, SourceIds = [s.Id] });
        }
        return roots;
    }

    internal static List<FileRecord> Snapshot(List<BackupRoot> roots, List<string>? exclusions, CancellationToken ct)
    {
        var records = new List<FileRecord>();
        foreach (var root in roots)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stack = new Stack<string>(); stack.Push(root.OriginalPath);
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var path = stack.Pop();
                var relative = path == root.OriginalPath ? "" : Path.GetRelativePath(root.OriginalPath, path);
                if (relative.Length > 0) PathSafety.ValidateRelative(relative);
                FileIO.ValidateSourceType(path);
                if (!names.Add(relative)) throw new BackupException("来源存在仅大小写不同的冲突名称，当前格式不能安全迁移。");
                if (records.Count >= InventoryStore.MaxEntries) throw new BackupException("超过一百万条文件清单上限，请缩小范围。");
                var attrs = File.GetAttributes(path);
                var dir = (attrs & FileAttributes.Directory) != 0;
                records.Add(new FileRecord { Id = Guid.NewGuid().ToString("N"), RootId = root.Id, RelativePath = relative, IsDirectory = dir,
                    Length = dir ? 0 : new FileInfo(path).Length, LastWriteUtcTicks = File.GetLastWriteTimeUtc(path).Ticks, Attributes = (int)attrs });
                if (dir)
                    foreach (var child in Directory.EnumerateFileSystemEntries(path)) stack.Push(child);
            }
        }
        return records;
    }

    internal static string SourcePath(BackupRoot root, FileRecord file) => file.RelativePath.Length == 0 ? root.OriginalPath : PathSafety.Under(root.OriginalPath, file.RelativePath);

    internal static async Task ValidateSnapshotAsync(List<BackupRoot> roots, List<FileRecord> expected, CancellationToken ct)
    {
        var actual = Snapshot(roots, null, ct);
        var map = actual.ToDictionary(f => f.RootId + "|" + f.RelativePath, StringComparer.OrdinalIgnoreCase);
        if (map.Count != expected.Count) throw new BackupException("备份期间来源目录发生增删，请关闭写入程序后重试。");
        var byRoot = roots.ToDictionary(r => r.Id);
        foreach (var f in expected)
        {
            ct.ThrowIfCancellationRequested();
            if (!map.TryGetValue(f.RootId + "|" + f.RelativePath, out var a) || f.IsDirectory != a.IsDirectory || f.Length != a.Length || f.LastWriteUtcTicks != a.LastWriteUtcTicks || f.Attributes != a.Attributes)
                throw new BackupException("备份期间来源文件或元数据变化，未发布完整备份。");
            if (!f.IsDirectory && !string.Equals(await FileIO.HashAsync(SourcePath(byRoot[f.RootId], f), ct), f.Sha256, StringComparison.Ordinal))
                throw new BackupException("备份期间来源内容变化，未发布完整备份。");
        }
    }

    private static void WriteReports(string path, BackupManifest manifest)
    {
        var reports = Path.Combine(path, "reports"); Directory.CreateDirectory(reports);
        var lines = new List<string> { "Codex 备份报告", $"备份 ID：{manifest.BackupId}", $"创建时间：{manifest.CreatedUtc:O}", $"文件：{manifest.FileCount:N0}，字节：{manifest.TotalBytes:N0}", "范围及限制：" };
        lines.AddRange(manifest.CoverageNotes); lines.Add("排除项目："); lines.AddRange(manifest.Exclusions);
        var body = string.Join(Environment.NewLine, lines);
        File.WriteAllText(Path.Combine(reports, "summary.txt"), body, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(reports, "summary.html"), "<!doctype html><meta charset=utf-8><title>Codex 备份报告</title><style>body{font:16px 'Microsoft YaHei',sans-serif;max-width:960px;margin:48px auto;line-height:1.7}pre{white-space:pre-wrap;overflow-wrap:anywhere}</style><h1>Codex 备份报告</h1><pre>" + WebUtility.HtmlEncode(body) + "</pre>", new UTF8Encoding(false));
        FileIO.WriteJsonDurable(Path.Combine(reports, "exclusions.json"), manifest.Exclusions);
    }
}

internal sealed class CompletionMarker
{
    public int FormatVersion { get; set; } = 1;
    public string BackupId { get; set; } = "";
    public string ManifestSha256 { get; set; } = "";
    public string InventorySha256 { get; set; } = "";
}
