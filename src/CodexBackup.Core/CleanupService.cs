using System.Text.Json;

namespace CodexBackup.Core;

public static class CleanupService
{
    private static readonly HashSet<string> GeneratedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", ".cache", ".codex-cache", ".codex-temp", ".pytest_cache", "__pycache__", ".next", "dist", "build", "target"
    };

    public static IReadOnlyList<CleanupCandidate> FindCandidates(IEnumerable<SessionReference> sessions, CancellationToken cancellationToken = default)
    {
        var references = sessions.Where(session => session.Lifecycle == SessionLifecycle.Archived && !string.IsNullOrWhiteSpace(session.ProjectPath)).ToList();
        var activeProjects = sessions
            .Where(session => session.Lifecycle == SessionLifecycle.Active && !string.IsNullOrWhiteSpace(session.ProjectPath))
            .Select(session => SafeCanonical(session.ProjectPath))
            .Where(path => path is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = new Dictionary<string, CleanupCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var session in references)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var project = SafeCanonical(session.ProjectPath);
            if (project is null || !Directory.Exists(project)) continue;
            var shared = activeProjects.Any(active => SafeContains(project, active) || SafeContains(active, project));
            if (!IsProtectedProjectRoot(project))
            {
                if (!candidates.TryGetValue(project, out var wholeProject))
                {
                    var projectEstimate = Estimate(project, cancellationToken);
                    wholeProject = new CleanupCandidate
                    {
                        Kind = CleanupCandidateKind.ArchivedProject,
                        CandidatePath = project,
                        ProjectPath = project,
                        Reason = "归档会话留下的完整项目。此项包含源码，只会在已验证备份覆盖后移动到隔离区。",
                        EstimatedBytes = projectEstimate.Bytes,
                        FileCount = projectEstimate.Files,
                        HasGit = Directory.Exists(Path.Combine(project, ".git")) || File.Exists(Path.Combine(project, ".git")),
                        LastModifiedUtc = LastWrite(project),
                        IsSharedWithActiveSession = shared
                    };
                    candidates.Add(project, wholeProject);
                }
                if (!wholeProject.SessionIds.Contains(session.Id, StringComparer.OrdinalIgnoreCase)) wholeProject.SessionIds.Add(session.Id);
            }
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(project, "*", SearchOption.TopDirectoryOnly).ToList(); }
            catch { continue; }
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(entry);
                if (!Directory.Exists(entry) || !GeneratedDirectories.Contains(name)) continue;
                var full = SafeCanonical(entry);
                if (full is null) continue;
                if (!candidates.TryGetValue(full, out var candidate))
                {
                    var estimate = Estimate(full, cancellationToken);
                    candidate = new CleanupCandidate
                    {
                        Kind = CleanupCandidateKind.GeneratedContent,
                        CandidatePath = full,
                        ProjectPath = project,
                        Reason = $"归档会话的常见可重建目录：{name}。不会删除项目根目录、源码、会话或记忆。",
                        EstimatedBytes = estimate.Bytes,
                        FileCount = estimate.Files,
                        HasGit = Directory.Exists(Path.Combine(project, ".git")) || File.Exists(Path.Combine(project, ".git")),
                        LastModifiedUtc = LastWrite(full),
                        IsSharedWithActiveSession = shared
                    };
                    candidates.Add(full, candidate);
                }
                if (!candidate.SessionIds.Contains(session.Id, StringComparer.OrdinalIgnoreCase)) candidate.SessionIds.Add(session.Id);
            }
        }
        return candidates.Values.OrderByDescending(candidate => candidate.IsSharedWithActiveSession).ThenByDescending(candidate => candidate.ContainsSource).ThenBy(candidate => candidate.CandidatePath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static async Task<CleanupResult> QuarantineAsync(IEnumerable<CleanupCandidate> candidates, CancellationToken cancellationToken = default)
    {
        var selected = new List<CleanupCandidate>();
        foreach (var candidate in candidates.Where(candidate => candidate.Selected && candidate.CanQuarantine).OrderBy(candidate => candidate.CandidatePath.Length))
        {
            if (selected.Any(parent => SafeContains(parent.CandidatePath, candidate.CandidatePath))) continue;
            selected.Add(candidate);
        }
        if (selected.Count == 0) return new([], [], "");
        var moved = new List<CleanupJournalEntry>();
        var failed = new List<string>();
        foreach (var candidate in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var source = PathSafety.Full(candidate.CandidatePath);
                var project = PathSafety.Full(candidate.ProjectPath);
                var relative = Path.GetRelativePath(project, source);
                var validGenerated = candidate.Kind == CleanupCandidateKind.GeneratedContent && !string.IsNullOrWhiteSpace(relative) && relative != "." && !relative.Contains(Path.DirectorySeparatorChar) && !relative.Contains(Path.AltDirectorySeparatorChar) && GeneratedDirectories.Contains(Path.GetFileName(source)) && PathSafety.Contains(project, source);
                var validProject = candidate.Kind == CleanupCandidateKind.ArchivedProject && PathsEqual(source, project) && !IsProtectedProjectRoot(source);
                if (!validGenerated && !validProject) throw new BackupException("清理候选不符合归档项目或可重建目录边界，已拒绝隔离。");
                if (!Directory.Exists(source) && !File.Exists(source)) continue;
                var quarantineRoot = Path.Combine(Path.GetDirectoryName(project)!, ".codex-backup-quarantine", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
                var target = candidate.Kind == CleanupCandidateKind.ArchivedProject
                    ? Path.Combine(quarantineRoot, Path.GetFileName(project))
                    : Path.Combine(quarantineRoot, Path.GetFileName(project), Path.GetFileName(source));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (Directory.Exists(source)) Directory.Move(source, target);
                else File.Move(source, target);
                moved.Add(new CleanupJournalEntry { Kind = candidate.Kind, OriginalPath = source, ProjectPath = project, QuarantinePath = target, SessionIds = candidate.SessionIds.ToList() });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BackupException or ArgumentException)
            { failed.Add(candidate.CandidatePath + "：" + ex.Message); }
        }
        if (moved.Count == 0) return new([], failed, "");
        var journalDirectory = Path.GetDirectoryName(moved[0].QuarantinePath)!;
        var journalPath = Path.Combine(journalDirectory, "cleanup-journal.json");
        FileIO.WriteJsonDurable(journalPath, new CleanupJournal { CreatedUtc = DateTimeOffset.UtcNow, Entries = moved });
        await Task.CompletedTask;
        return new(moved.Select(entry => entry.QuarantinePath).ToList(), failed, journalPath);
    }

    public static async Task<IReadOnlyList<string>> RestoreAsync(string journalPath, CancellationToken cancellationToken = default)
    {
        var journal = FileIO.ReadJson<CleanupJournal>(journalPath, 1024 * 1024);
        var restored = new List<string>();
        foreach (var entry in journal.Entries.AsEnumerable().Reverse())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var original = PathSafety.Full(entry.OriginalPath);
            var project = PathSafety.Full(entry.ProjectPath);
            var quarantined = PathSafety.Full(entry.QuarantinePath);
            var relative = Path.GetRelativePath(project, original);
            var validGenerated = entry.Kind == CleanupCandidateKind.GeneratedContent && !string.IsNullOrWhiteSpace(relative) && relative != "." && !relative.Contains(Path.DirectorySeparatorChar) && !relative.Contains(Path.AltDirectorySeparatorChar) && GeneratedDirectories.Contains(Path.GetFileName(original));
            var validProject = entry.Kind == CleanupCandidateKind.ArchivedProject && PathsEqual(original, project) && !IsProtectedProjectRoot(original);
            if ((!validGenerated && !validProject)
                || !quarantined.Contains(Path.DirectorySeparatorChar + ".codex-backup-quarantine" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new BackupException("隔离日志包含不受支持的路径，已停止还原。");
            if (Directory.Exists(original) || File.Exists(original)) throw new BackupException($"原位置已经存在，未覆盖：{original}");
            if (!Directory.Exists(quarantined) && !File.Exists(quarantined)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(original)!);
            if (Directory.Exists(quarantined)) Directory.Move(quarantined, original); else File.Move(quarantined, original);
            restored.Add(original);
        }
        await Task.CompletedTask;
        return restored;
    }

    private static (long Bytes, long Files) Estimate(string path, CancellationToken cancellationToken)
    {
        long total = 0, count = 0;
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Offline };
            foreach (var file in Directory.EnumerateFiles(path, "*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { total = checked(total + new FileInfo(file).Length); count++; } catch { }
                if (total < 0) return (long.MaxValue, count);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { return (0, count); }
        return (total, count);
    }

    private static DateTimeOffset? LastWrite(string path)
    {
        try { return Directory.GetLastWriteTimeUtc(path); } catch { return null; }
    }

    private static string? SafeCanonical(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return MigrationCoverage.Canonical(path); } catch (Exception ex) when (ex is BackupException or ArgumentException) { return null; }
    }

    private static bool SafeContains(string parent, string child)
    {
        try { return PathSafety.Contains(parent, child); } catch (Exception ex) when (ex is BackupException or ArgumentException) { return false; }
    }

    private static bool PathsEqual(string left, string right) => string.Equals(MigrationCoverage.Canonical(left), MigrationCoverage.Canonical(right), StringComparison.OrdinalIgnoreCase);

    private static bool IsProtectedProjectRoot(string project)
    {
        try
        {
            var full = PathSafety.Full(project);
            if (string.Equals(full, Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase)) return true;
            try { PathSafety.RejectSystemTarget(full); } catch (BackupException) { return true; }
            foreach (var protectedPath in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            })
                if (!string.IsNullOrWhiteSpace(protectedPath) && PathsEqual(full, protectedPath)) return true;
            return false;
        }
        catch (Exception ex) when (ex is BackupException or ArgumentException or IOException or UnauthorizedAccessException) { return true; }
    }

    private sealed class CleanupJournal
    {
        public DateTimeOffset CreatedUtc { get; set; }
        public List<CleanupJournalEntry> Entries { get; set; } = [];
    }

    private sealed class CleanupJournalEntry
    {
        public CleanupCandidateKind Kind { get; set; }
        public string OriginalPath { get; set; } = "";
        public string ProjectPath { get; set; } = "";
        public string QuarantinePath { get; set; } = "";
        public List<string> SessionIds { get; set; } = [];
    }
}
