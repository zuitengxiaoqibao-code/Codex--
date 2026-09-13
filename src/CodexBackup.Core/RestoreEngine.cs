using System.Text.Json;

namespace CodexBackup.Core;

public sealed class RestoreEngine
{
    internal Action<string>? Checkpoint { get; init; }
    public async Task<RestorePreview> PreviewAsync(RestoreRequest request, CancellationToken cancellationToken = default)
    {
        var package = await VerifyPackageAsync(request, cancellationToken: cancellationToken);
        try { return BuildPreview(request, package); }
        finally { EncryptedPackage.CleanupExtractedPackage(package.PackagePath); }
    }

    private static Task<VerifiedPackage> VerifyPackageAsync(RestoreRequest request, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (EncryptedPackage.IsEncryptedFile(request.PackagePath))
        {
            if (string.IsNullOrWhiteSpace(request.EncryptionPassword)) throw new BackupException("这是加密备份，请输入创建备份时设置的密码；密码丢失后无法恢复。");
            return new PackageVerifier().VerifyAsync(request.PackagePath, request.EncryptionPassword, progress, cancellationToken);
        }
        return new PackageVerifier().VerifyAsync(request.PackagePath, progress, cancellationToken: cancellationToken);
    }

    internal static RestorePreview BuildPreview(RestoreRequest request, VerifiedPackage package)
    {
        var findings = new List<Finding>();
        var items = new List<RestorePreviewItem>();
        var rootMap = package.Manifest.Roots.ToDictionary(r => r.Id);
        try {RestorePlanner.ValidateCoverage(package,request);} catch(BackupException ex) {findings.Add(new(FindingLevel.Blocker,"COMPLETE_COVERAGE",ex.Message));}
        try { RestorePlanner.ValidateCoreSelection(package.Manifest, request); }
        catch (BackupException ex) { findings.Add(new(FindingLevel.Blocker, "CORE_SELECTION", ex.Message)); }
        if (request.Mappings.Count == 0 || request.Mappings.Count > rootMap.Count || request.Mappings.Select(m => m.RootId).Distinct().Count() != request.Mappings.Count)
            findings.Add(new(FindingLevel.Blocker, "MAPPINGS", "至少选择一个根目录；同一根目录不能映射两次。"));
        foreach (var mapping in request.Mappings)
        {
            try
            {
                if (!rootMap.TryGetValue(mapping.RootId, out var root)) throw new BackupException("恢复映射包含未知来源。");
                var target = PathSafety.Full(mapping.TargetPath);
                PathSafety.RejectSystemTarget(target);
                var managedCore = !request.Isolated && root.Kind == SourceKind.Core && CoreRestoreAdapter.IsCandidate(root, package.Files);
                if (WriterGuard.IsProtectedLocation(target) && !(managedCore && WriterGuard.IsCodexHome(target)))
                    throw new BackupException("目标属于活动应用数据位置；仅已知格式的 Codex 核心受控恢复可写入明确的 CODEX_HOME，其他项请选择独立目录。");
                PathSafety.RejectReparseAncestors(target);
                PathSafety.RequireNtfs(target);
                if (PathSafety.Contains(target, package.PackagePath) || PathSafety.Contains(package.PackagePath, target))
                    throw new BackupException("恢复目标不能包含备份包，也不能位于备份包内部。");
                if (Environment.ProcessPath is { } exe && PathSafety.Contains(target, exe)) throw new BackupException("不能替换本工具正在运行的目录。");
                if (items.Any(i => PathSafety.Contains(i.TargetPath, target) || PathSafety.Contains(target, i.TargetPath))) throw new BackupException("恢复目标不能互相重叠。");
                var exists = File.Exists(target) || Directory.Exists(target);
                if (exists && !request.ReplaceExisting) throw new BackupException("目标已存在；请选择新目录，或明确启用完整替换并保留回滚。");
                if (root.Kind is SourceKind.Core or SourceKind.Application or SourceKind.Tool)
                {
                    if (root.Kind == SourceKind.Core && !request.Isolated)
                    {
                        if (!managedCore) throw new BackupException("该核心目录结构不受支持，请使用隔离恢复，不能猜测状态迁移格式。");
                        findings.Add(new(FindingLevel.Warning, "CORE_ADAPT", $"{root.Name} 将在暂存区验证状态结构并重连已知路径；配置、凭据、技能、插件及自动化移入停用区。未知结构会在替换前阻止。"));
                    }
                    else findings.Add(new(FindingLevel.Warning, "ACTIVATION_PENDING", $"{root.Name} 将原样隔离恢复；不会自动启用程序、自动化或登录状态。"));
                }
                items.Add(new(root.Id, root.Name, target, exists, exists ? "完整替换；先复制、校验并保留原数据" : "创建并逐文件校验"));
            }
            catch (Exception ex) when (ex is BackupException or IOException or UnauthorizedAccessException or ArgumentException)
            { findings.Add(new(FindingLevel.Blocker, "TARGET", ex.Message, mapping.TargetPath)); }
        }
        if (request.Isolated) findings.Add(new(FindingLevel.Info, "ISOLATED", "隔离模式不改写历史文本和内部路径，不自动运行恢复出的文件。应用层验收仍待完成。"));
        return new(items, findings, package.Files.Where(f => request.Mappings.Any(m => m.RootId == f.RootId)).Sum(f => f.Length));
    }

    public async Task<RestoreResult> RestoreAsync(RestoreRequest request, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var ct = cancellationToken;
        ct.ThrowIfCancellationRequested();
        var package = await VerifyPackageAsync(request, progress, ct);
        var preview = BuildPreview(request, package);
        if (!preview.CanProceed) throw new BackupException(string.Join(Environment.NewLine, preview.Findings.Where(f => f.Level == FindingLevel.Blocker).Select(f => f.Message)));
        var journal = new RestoreJournal();
        var rootMap = package.Manifest.Roots.ToDictionary(r => r.Id);
        var adaptationNotes = new List<string>();
        if (!request.Isolated && package.Manifest.Roots.Any(r => r.Kind == SourceKind.Core && request.Mappings.Any(m => m.RootId == r.Id)))
            WriterGuard.RequireStopped([SourceKind.Core]);
        foreach (var item in preview.Items)
        {
            var parent = Path.GetDirectoryName(item.TargetPath)!;
            PathSafety.RejectReparseAncestors(parent);
            Directory.CreateDirectory(parent);
            var prefix = Path.Combine(parent, ".codex-restore-" + journal.Id + "-" + journal.Entries.Count);
            var files = package.Files.Where(f => f.RootId == item.RootId).ToList();
            journal.Entries.Add(new RestoreJournalEntry
            {
                RootId = item.RootId, Target = item.TargetPath, Stage = prefix + ".stage", Rollback = prefix + ".rollback", Retired = prefix + ".original", Undo = prefix + ".undo",
                IsDirectory = rootMap[item.RootId].IsDirectory, HadOriginal = item.Exists, RestoredFiles = files,
                ManagedCore = !request.Isolated && rootMap[item.RootId].Kind == SourceKind.Core
            });
        }
        var journalPath = Path.Combine(Path.GetDirectoryName(preview.Items[0].TargetPath)!, ".codex-restore-" + journal.Id + ".journal.json");
        SaveJournal(journalPath, journal);
        try
        {
        long bytes = 0, count = 0;
        // Stage and validate every target, then verify each independent rollback copy BEFORE touching originals.
        foreach (var e in journal.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (e.IsDirectory) Directory.CreateDirectory(e.Stage);
            await MaterializeAsync(package.PackagePath, e.Stage, e.RestoredFiles, p => { bytes += p; count++; progress?.Report(new("准备恢复", $"已校验 {count:N0} 个文件", count, bytes, preview.TotalBytes)); }, ct);
            if (e.ManagedCore || request.RequireCompleteMigration)
            {
                var pathMappings = RestorePlanner.BuildPathMappings(package.Manifest,request.Mappings);
                adaptationNotes.AddRange(await RestorePlanner.PrepareStructuralFilesAsync(e.Stage,rootMap[e.RootId],e.RestoredFiles,package,pathMappings,ct));
                if(e.ManagedCore) using (DirectoryLease.Acquire(e.Stage))
                    adaptationNotes.AddRange(await new CoreRestoreAdapter().PrepareAsync(e.Stage, rootMap[e.RootId].OriginalPath, pathMappings, ct));
                var stagedRoot = new BackupRoot { Id = e.RootId, OriginalPath = e.Stage, IsDirectory = e.IsDirectory, Kind = SourceKind.Custom };
                e.RestoredFiles = BackupEngine.Snapshot([stagedRoot], null, ct);
                foreach (var file in e.RestoredFiles.Where(f => !f.IsDirectory)) file.Sha256 = await FileIO.HashAsync(BackupEngine.SourcePath(stagedRoot, file), ct);
                await MatchTreeAsync(e.Stage, e.RestoredFiles, ct);
            }
            e.State = "Staged"; SaveJournal(journalPath, journal);
            if (e.HadOriginal)
            {
                var originalRoot = new BackupRoot { Id = e.RootId, OriginalPath = e.Target, IsDirectory = Directory.Exists(e.Target), Kind = SourceKind.Custom };
                e.OriginalIsDirectory = originalRoot.IsDirectory;
                e.OriginalFiles = BackupEngine.Snapshot([originalRoot], null, ct);
                var required = e.OriginalFiles.Sum(f => f.Length);
                if (new DriveInfo(Path.GetPathRoot(e.Target)!).AvailableFreeSpace < required + 64L * 1024 * 1024) throw new BackupException("没有足够空间保存完整回滚副本。");
                if (e.OriginalIsDirectory) Directory.CreateDirectory(e.Rollback);
                foreach (var f in e.OriginalFiles.OrderBy(f => f.RelativePath.Length))
                {
                    ct.ThrowIfCancellationRequested();
                    var dest = Destination(e.Rollback, f);
                    if (f.IsDirectory) Directory.CreateDirectory(dest);
                    else
                    {
                        var source = BackupEngine.SourcePath(originalRoot, f);
                        f.Sha256 = await FileIO.HashAsync(source, ct);
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        await FileIO.CopyVerifiedAsync(source, dest, f.Sha256, ct);
                    }
                }
                ApplyMetadata(e.Rollback, e.OriginalFiles);
                await MatchTreeAsync(e.Rollback, e.OriginalFiles, ct);
                await BackupEngine.ValidateSnapshotAsync([originalRoot], e.OriginalFiles, ct);
            }
            e.State = "Prepared"; SaveJournal(journalPath, journal);
            Checkpoint?.Invoke("Prepared");
        }
        ct.ThrowIfCancellationRequested();
        // Re-check all originals before the first switch. Any new file prevents replacement.
        RestorePlanner.ValidateCoverage(package,request,journal.Entries.ToDictionary(e=>e.RootId,e=>e.Stage));
        foreach (var e in journal.Entries)
        {
            PathSafety.RejectReparseAncestors(e.Target);
            if (e.HadOriginal) await MatchTreeAsync(e.Target, e.OriginalFiles, ct);
            else if (Exists(e.Target)) throw new BackupException("预演后目标位置出现新数据，已停止恢复。");
        }
        foreach (var e in journal.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (e.ManagedCore) WriterGuard.RequireStopped([SourceKind.Core]);
            using var parentLease = DirectoryLease.Acquire(Path.GetDirectoryName(e.Target)!);
            e.State = "MovingOriginal"; SaveJournal(journalPath, journal);
            Checkpoint?.Invoke("BeforeOriginalMove");
            if (e.HadOriginal) Move(e.Target, e.Retired);
            Checkpoint?.Invoke("AfterOriginalMove");
            e.State = "Installing"; SaveJournal(journalPath, journal);
            Move(e.Stage, e.Target);
            Checkpoint?.Invoke("AfterTargetInstall");
            e.State = "Applied"; SaveJournal(journalPath, journal);
            await MatchTreeAsync(e.Target, e.RestoredFiles, ct);
        }
        RestorePlanner.ValidateCoverage(package,request,journal.Entries.ToDictionary(e=>e.RootId,e=>e.Target),adaptationNotes);
        journal.Status = "Complete"; SaveJournal(journalPath, journal);
        var notes = preview.Findings.Select(f => f.Message).ToList();
        notes.AddRange(adaptationNotes);
        if(request.RequireCompleteMigration)notes.Add("完整迁移的会话正文与源码目录关联已在暂存及最终位置检查；项目运行环境和 Codex 界面仍需实际打开验收。");
        var acceptance = RestoreAcceptance.Validate(package, request, journal.Entries.ToDictionary(e => e.RootId, e => e.Target, StringComparer.Ordinal));
        notes.Add(acceptance.Summary);
        notes.AddRange(acceptance.Checks.Where(c => c.Level == FindingLevel.Blocker).Select(c => c.Message));
        notes.Add("Codex 登录、侧栏会话、记忆读取和项目运行仍需人工验收；配置、技能、插件及外围工具不会自动启用。");
        return new(journalPath, journal.Entries.Select(e => e.Target).ToList(), journal.Entries.Where(e => e.HadOriginal).Select(e => e.Rollback).ToList(), notes, acceptance);
        }
        catch (Exception ex)
        {
            EncryptedPackage.CleanupExtractedPackage(package.PackagePath);
            throw new BackupException($"恢复未完成（{ex.GetType().Name}）。请勿删除辅助目录；部分步骤可能已经完成。可在“检查 → 回滚恢复”选择日志：{journalPath}。原因：{ex.Message}");
        }
        finally { EncryptedPackage.CleanupExtractedPackage(package.PackagePath); }
    }

    public async Task RollbackAsync(string journalPath, CancellationToken cancellationToken = default)
    {
        var ct = cancellationToken;
        var envelope = FileIO.ReadJson<JournalEnvelope>(PathSafety.Full(journalPath), 256L * 1024 * 1024);
        var journal = JournalProtection.Open(envelope);
        if (journal.Version != 1 || journal.Entries.Count is < 1 or > 10000 || !Guid.TryParseExact(journal.Id, "N", out _)) throw new BackupException("回滚日志无效。");
        // All paths and fingerprints are checked before any mutation. Journals are DPAPI sealed to this account.
        var recoverySources = new Dictionary<RestoreJournalEntry, string>();
        for (var i = 0; i < journal.Entries.Count; i++)
        {
            var e = journal.Entries[i];
            PathSafety.RejectSystemTarget(e.Target);
            if (WriterGuard.IsProtectedLocation(e.Target) && !(e.ManagedCore && WriterGuard.IsCodexHome(e.Target))) throw new BackupException("回滚目标属于未授权的活动应用状态。");
            if (e.ManagedCore) WriterGuard.RequireStopped([SourceKind.Core]);
            var prefix = Path.Combine(Path.GetDirectoryName(PathSafety.Full(e.Target))!, ".codex-restore-" + journal.Id + "-" + i);
            if (e.Stage != prefix + ".stage" || e.Rollback != prefix + ".rollback" || e.Retired != prefix + ".original" || e.Undo != prefix + ".undo") throw new BackupException("回滚日志路径校验失败。");
            foreach (var p in new[] { e.Target, e.Rollback, e.Retired, e.Stage, e.Undo }) PathSafety.RejectReparseAncestors(p);
            if (e.State is "RolledBack" or "Planned" or "Staged") continue;
            if (Exists(e.Target))
            {
                if (e.HadOriginal && await MatchesAsync(e.Target, e.OriginalFiles, ct)) continue;
                if (!await MatchesAsync(e.Target, e.RestoredFiles, ct)) throw new BackupException("恢复后的文件已变化；为保护新工作，自动回滚被阻止。请先另行备份当前数据。");
            }
            if (e.HadOriginal)
            {
                var candidate = await MatchesAsync(e.Retired, e.OriginalFiles, ct) ? e.Retired :
                    await MatchesAsync(e.Rollback, e.OriginalFiles, ct) ? e.Rollback : null;
                if (candidate is null) throw new BackupException("原数据回滚副本缺失或损坏。");
                recoverySources[e] = candidate;
            }
        }
        Checkpoint?.Invoke("RollbackPrepared");
        foreach (var e in journal.Entries.AsEnumerable().Reverse())
        {
            ct.ThrowIfCancellationRequested();
            if (e.ManagedCore) WriterGuard.RequireStopped([SourceKind.Core]);
            using var parentLease = DirectoryLease.Acquire(Path.GetDirectoryName(e.Target)!);
            if (e.State is "RolledBack" or "Planned" or "Staged") continue;
            if (e.HadOriginal && Exists(e.Target) && await MatchesAsync(e.Target, e.OriginalFiles, ct))
            { e.State = "RolledBack"; SaveJournal(journalPath, journal); continue; }
            if (Exists(e.Target)) await MatchTreeAsync(e.Target, e.RestoredFiles, ct);
            if (e.HadOriginal) await MatchTreeAsync(recoverySources[e], e.OriginalFiles, ct);
            e.State = "RollingBack"; SaveJournal(journalPath, journal);
            if (Exists(e.Target)) Move(e.Target, e.Undo);
            if (e.HadOriginal) Move(recoverySources[e], e.Target);
            if (e.HadOriginal) await MatchTreeAsync(e.Target, e.OriginalFiles, ct);
            else if (Exists(e.Target)) throw new BackupException("回滚后目标意外出现数据，未标记成功。");
            e.State = "RolledBack"; SaveJournal(journalPath, journal);
        }
        journal.Status = "RolledBack"; SaveJournal(journalPath, journal);
    }

    internal static async Task MaterializeAsync(string package, string target, IReadOnlyList<FileRecord> files, Action<long>? progress, CancellationToken ct)
    {
        foreach (var f in files.OrderBy(f => f.RelativePath.Length))
        {
            ct.ThrowIfCancellationRequested();
            var dest = Destination(target, f);
            if (f.IsDirectory) Directory.CreateDirectory(dest);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                await FileIO.CopyVerifiedAsync(PathSafety.Under(Path.Combine(package, "payload"), f.Id), dest, f.Sha256, ct);
                progress?.Invoke(f.Length);
            }
        }
        ApplyMetadata(target, files);
        await MatchTreeAsync(target, files, ct);
    }

    private static void ApplyMetadata(string target, IReadOnlyList<FileRecord> files)
    {
        foreach (var f in files.OrderByDescending(f => f.RelativePath.Length))
        {
            var path = Destination(target, f);
            if (f.IsDirectory) Directory.SetLastWriteTimeUtc(path, new DateTime(f.LastWriteUtcTicks, DateTimeKind.Utc));
            else File.SetLastWriteTimeUtc(path, new DateTime(f.LastWriteUtcTicks, DateTimeKind.Utc));
            var safe = (FileAttributes)f.Attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.Archive | FileAttributes.System);
            if (safe != 0) File.SetAttributes(path, safe);
        }
    }

    internal static async Task MatchTreeAsync(string target, IReadOnlyList<FileRecord> expected, CancellationToken ct)
    {
        if (expected.Count == 0 || !Exists(target)) throw new BackupException("待校验目录缺失或清单为空。");
        var root = expected.Single(f => f.RelativePath.Length == 0);
        var actual = BackupEngine.Snapshot([new BackupRoot { Id = root.RootId, OriginalPath = target, IsDirectory = root.IsDirectory, Kind = SourceKind.Custom }], null, ct);
        var map = actual.ToDictionary(f => f.RelativePath, StringComparer.OrdinalIgnoreCase);
        if (map.Count != expected.Count) throw new BackupException("目录内容数量已变化。");
        foreach (var f in expected)
        {
            if (!map.TryGetValue(f.RelativePath, out var a) || f.IsDirectory != a.IsDirectory || f.Length != a.Length) throw new BackupException("目录结构或大小已变化。");
            if (!f.IsDirectory && await FileIO.HashAsync(Destination(target, f), ct) != f.Sha256) throw new BackupException("文件内容已变化。");
        }
    }
    private static async Task<bool> MatchesAsync(string target, IReadOnlyList<FileRecord> expected, CancellationToken ct)
    {
        try { await MatchTreeAsync(target, expected, ct); return true; }
        catch (Exception ex) when (ex is BackupException or IOException or UnauthorizedAccessException) { return false; }
    }
    private static string Destination(string root, FileRecord f) => f.RelativePath.Length == 0 ? PathSafety.Full(root) : PathSafety.Under(root, f.RelativePath);
    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
    private static void Move(string source, string target)
    {
        PathSafety.RejectReparseAncestors(source); PathSafety.RejectReparseAncestors(target);
        if (Exists(target)) throw new BackupException("恢复辅助路径已存在，为避免覆盖已停止。");
        if (Directory.Exists(source)) Directory.Move(source, target); else File.Move(source, target);
    }
    private static void SaveJournal(string path, RestoreJournal journal)
    {
        var envelope = JournalProtection.Seal(journal);
        JournalProtection.ValidateSize(envelope);
        FileIO.WriteJsonDurable(path, envelope);
    }
}

internal sealed class RestoreJournal
{
    public int Version { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Status { get; set; } = "Preparing";
    public List<RestoreJournalEntry> Entries { get; set; } = [];
}
internal sealed class RestoreJournalEntry
{
    public string RootId { get; set; } = "";
    public string Target { get; set; } = "";
    public string Stage { get; set; } = "";
    public string Rollback { get; set; } = "";
    public string Retired { get; set; } = "";
    public string Undo { get; set; } = "";
    public bool IsDirectory { get; set; }
    public bool HadOriginal { get; set; }
    public bool ManagedCore { get; set; }
    public bool OriginalIsDirectory { get; set; }
    public string State { get; set; } = "Planned";
    public List<FileRecord> RestoredFiles { get; set; } = [];
    public List<FileRecord> OriginalFiles { get; set; } = [];
}
