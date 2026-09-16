using System.ComponentModel;

namespace CodexBackup.Core;

public enum SourceKind { Core, Project, Memory, Skill, Plugin, Tool, Application, Environment, Custom, Session }
public enum FindingLevel { Info, Warning, Blocker }
public enum SessionLifecycle { Active, Archived, Unknown }
public sealed record Finding(FindingLevel Level, string Code, string Message, string? Path = null);
public static class ProductInfo
{
    public const string Version = "0.3.6-preview";
}

public sealed class SourceItem : INotifyPropertyChanged
{
    private bool required;
    private bool selected = true;

    public event PropertyChangedEventHandler? PropertyChanged;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public SourceKind Kind { get; set; }
    public bool Required
    {
        get => required;
        set
        {
            if (required == value) return;
            required = value;
            OnChanged(nameof(Required), nameof(PriorityText), nameof(PriorityRank), nameof(HasProblem));
        }
    }
    public bool Selected
    {
        get => selected;
        set
        {
            if (selected == value) return;
            selected = value;
            OnChanged(nameof(Selected), nameof(StatusText), nameof(HasProblem));
        }
    }
    public bool Exists { get; set; }
    public bool IsDirectory { get; set; } = true;
    public string Reason { get; set; } = "";
    public string DiscoveredBy { get; set; } = "";
    public string Notes { get; set; } = "";
    public long? EstimatedBytes { get; set; }
    public DateTimeOffset? LastModifiedUtc { get; set; }
    public DateTimeOffset? LastActivityUtc { get; set; }
    public List<string> DependencyIds { get; set; } = [];
    public string PriorityText => Required ? "必须备份" : Kind is SourceKind.Project or SourceKind.Session or SourceKind.Memory ? "建议备份" : "可选保存";
    public int PriorityRank => Required ? 0 : Kind is SourceKind.Project or SourceKind.Session or SourceKind.Memory ? 1 : 2;
    public string StatusText => !Exists ? "找不到" : Selected ? "已选择" : "未选择";
    public bool HasProblem => !Exists || (Required && !Selected);

    private void OnChanged(params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames) PropertyChanged?.Invoke(this, new(propertyName));
    }
}

public sealed class ScanResult
{
    public string UserProfile { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public string UserName { get; set; } = Environment.UserName;
    public string MachineName { get; set; } = Environment.MachineName;
    public List<SourceItem> Items { get; set; } = [];
    public List<Finding> Findings { get; set; } = [];
    public List<string> InstallationPaths { get; set; } = [];
    public string CodexVersion { get; set; } = "未知";
    public List<SessionReference> Sessions { get; set; } = [];
    public EnvironmentManifest EnvironmentManifest { get; set; } = new();
    public int UniqueSessionCount => Sessions.Select(s => s.Id).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
    public int SessionAssociationCount => Sessions.Count;
    public int ProjectLocationCount => Items.Where(x => x.Kind == SourceKind.Project).Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();
}

public sealed class SessionReference
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string CorePath { get; set; } = "";
    public string ProjectPath { get; set; } = "";
    public string TranscriptPath { get; set; } = "";
    public DateTimeOffset? LastActivityUtc { get; set; }
    public SessionLifecycle Lifecycle { get; set; } = SessionLifecycle.Unknown;
    public bool Selected { get; set; } = true;
    public bool HasMissingTranscript => string.IsNullOrWhiteSpace(TranscriptPath) || !File.Exists(TranscriptPath);
    public bool HasMissingProject => string.IsNullOrWhiteSpace(ProjectPath) || !Directory.Exists(ProjectPath);
    public bool HasProjectResidue => Lifecycle == SessionLifecycle.Archived && !HasMissingProject;
    public bool IsComplete => !HasMissingTranscript && !HasMissingProject;
}

public sealed record SessionSelectionGroup(string Key, IReadOnlyList<SessionReference> References);

/// <summary>
/// Keeps session-to-source relationships in memory so checkbox changes do not rescan every row.
/// </summary>
public sealed class SelectionCoordinator
{
    private readonly Dictionary<string, HashSet<string>> sessionSources;
    private readonly Dictionary<string, HashSet<string>> sourceSessions;
    private readonly Dictionary<string, SourceItem> sources;
    private readonly Dictionary<string, int> selectedReferences = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> selectedSessionKeys = new(StringComparer.OrdinalIgnoreCase);

    private SelectionCoordinator(
        Dictionary<string, HashSet<string>> sessionSources,
        Dictionary<string, HashSet<string>> sourceSessions,
        Dictionary<string, SourceItem> sources,
        IEnumerable<SessionSelectionGroup> groups)
    {
        this.sessionSources = sessionSources;
        this.sourceSessions = sourceSessions;
        this.sources = sources;
        foreach (var group in groups)
            if (group.References.All(reference => reference.Selected))
            {
                selectedSessionKeys.Add(group.Key);
                AddSelected(group.Key);
            }
    }

    public IReadOnlyCollection<string> SourcesForSession(string key) =>
        sessionSources.TryGetValue(key, out var ids) ? ids : Array.Empty<string>();

    public IReadOnlyCollection<string> SessionsForSource(string sourceId) =>
        sourceSessions.TryGetValue(sourceId, out var ids) ? ids : Array.Empty<string>();

    public void ApplySessionSelection(string key, bool selected) => ApplySessionSelection(key, selected, selectedSessionKeys);

    public void ApplySessionSelection(string key, bool selected, ISet<string> selectedSessionKeys)
    {
        if (!sessionSources.TryGetValue(key, out var related))
        {
            if (selected) selectedSessionKeys.Add(key); else selectedSessionKeys.Remove(key);
            return;
        }

        var changed = selected ? selectedSessionKeys.Add(key) : selectedSessionKeys.Remove(key);
        if (!changed) return;
        if (selected) AddSelected(key); else RemoveSelected(key);

        foreach (var sourceId in related)
        {
            if (!sources.TryGetValue(sourceId, out var source)) continue;
            if (selected) source.Selected = true;
            else if (!source.Required && selectedReferences.GetValueOrDefault(sourceId) == 0) source.Selected = false;
        }
    }

    public void ApplySourceSelection(string sourceId, bool selected)
    {
        if (!sources.TryGetValue(sourceId, out var source) || source.Required) return;
        source.Selected = selected;
    }

    public void Reconcile(ISet<string> selectedSessionKeys)
    {
        selectedReferences.Clear();
        this.selectedSessionKeys.Clear();
        foreach (var key in selectedSessionKeys)
        {
            this.selectedSessionKeys.Add(key);
            AddSelected(key);
        }
    }

    public static SelectionCoordinator Build(
        IEnumerable<SessionSelectionGroup> groups,
        IEnumerable<SourceItem> sourceItems,
        IReadOnlyDictionary<string, string>? pathReplacements = null)
    {
        var groupList = groups.ToList();
        var sourceList = sourceItems.ToList();
        var sourceMap = sourceList.ToDictionary(source => source.Id, StringComparer.OrdinalIgnoreCase);
        var byId = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var reverse = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groupList)
        {
            var related = FindRelatedSources(group, sourceList, pathReplacements ?? new Dictionary<string, string>());
            byId[group.Key] = related;
            foreach (var sourceId in related)
            {
                if (!reverse.TryGetValue(sourceId, out var users)) reverse[sourceId] = users = new(StringComparer.OrdinalIgnoreCase);
                users.Add(group.Key);
            }
        }
        return new SelectionCoordinator(byId, reverse, sourceMap, groupList);
    }

    private void AddSelected(string key)
    {
        foreach (var sourceId in sessionSources.GetValueOrDefault(key, []))
            selectedReferences[sourceId] = selectedReferences.GetValueOrDefault(sourceId) + 1;
    }

    private void RemoveSelected(string key)
    {
        foreach (var sourceId in sessionSources.GetValueOrDefault(key, []))
        {
            var count = selectedReferences.GetValueOrDefault(sourceId) - 1;
            if (count <= 0) selectedReferences.Remove(sourceId); else selectedReferences[sourceId] = count;
        }
    }

    private static HashSet<string> FindRelatedSources(SessionSelectionGroup group, IReadOnlyList<SourceItem> sourceList, IReadOnlyDictionary<string, string> replacements)
    {
        var direct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sourceList)
            if (group.References.Any(reference => IsRelated(source, reference, replacements))) direct.Add(source.Id);

        var related = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(direct);
        while (queue.Count > 0)
        {
            var sourceId = queue.Dequeue();
            var source = sourceList.FirstOrDefault(candidate => candidate.Id.Equals(sourceId, StringComparison.OrdinalIgnoreCase));
            if (!related.Add(sourceId) || source is null) continue;
            foreach (var dependencyId in source.DependencyIds)
                if (!related.Contains(dependencyId)) queue.Enqueue(dependencyId);
        }
        foreach (var source in sourceList.Where(source => source.Required)) related.Add(source.Id);
        return related;
    }

    private static bool IsRelated(SourceItem source, SessionReference reference, IReadOnlyDictionary<string, string> replacements)
    {
        try
        {
            var path = MigrationCoverage.Canonical(source.Path);
            if (source.Kind == SourceKind.Core && !string.IsNullOrWhiteSpace(reference.CorePath) && PathsEqual(path, MigrationCoverage.Resolve(reference.CorePath, replacements))) return true;
            if (source.Kind == SourceKind.Session && !string.IsNullOrWhiteSpace(reference.TranscriptPath) && PathsEqual(path, MigrationCoverage.Resolve(reference.TranscriptPath, replacements))) return true;
            if (source.Kind == SourceKind.Project && !string.IsNullOrWhiteSpace(reference.ProjectPath))
            {
                var project = MigrationCoverage.Resolve(reference.ProjectPath, replacements);
                return PathsEqual(path, project) || source.IsDirectory && PathSafety.Contains(path, project);
            }
        }
        catch (Exception ex) when (ex is BackupException or ArgumentException) { }
        return false;
    }

    private static bool PathsEqual(string left, string right) => string.Equals(left, MigrationCoverage.Canonical(right), StringComparison.OrdinalIgnoreCase);
}

public enum CleanupCandidateKind { GeneratedContent, ArchivedProject }

public sealed class CleanupCandidate
{
    public CleanupCandidateKind Kind { get; init; }
    public string CandidatePath { get; init; } = "";
    public string ProjectPath { get; init; } = "";
    public List<string> SessionIds { get; init; } = [];
    public string SessionText => string.Join("、", SessionIds.Take(3)) + (SessionIds.Count > 3 ? $" 等 {SessionIds.Count} 个" : "");
    public string Reason { get; init; } = "";
    public long EstimatedBytes { get; init; }
    public long FileCount { get; init; }
    public bool HasGit { get; init; }
    public string GitText => HasGit ? "有 Git" : "未识别 Git";
    public DateTimeOffset? LastModifiedUtc { get; init; }
    public bool IsSharedWithActiveSession { get; init; }
    public bool ContainsSource => Kind == CleanupCandidateKind.ArchivedProject;
    public string KindText => ContainsSource ? "整个归档项目" : "可重建目录";
    public string RiskText => IsSharedWithActiveSession ? "禁止清理" : ContainsSource ? "高风险：包含源码" : "低风险：可重建";
    public bool SafeToQuarantine => !IsSharedWithActiveSession;
    public bool IncludedInVerifiedBackup { get; set; }
    public bool CanQuarantine => SafeToQuarantine && IncludedInVerifiedBackup;
    public bool Selected { get; set; }
    public string StatusText => IsSharedWithActiveSession ? "活动会话仍在使用，禁止清理" : IncludedInVerifiedBackup ? "已随本次备份校验，可隔离" : "等待备份并校验";
}

public sealed record CleanupResult(IReadOnlyList<string> QuarantinedPaths, IReadOnlyList<string> FailedPaths, string JournalPath);

public sealed record OperationProgress(string Phase, string Message, long Files = 0, long Bytes = 0, long? TotalBytes = null);
public sealed class BackupRequest
{
    public List<SourceItem> Sources { get; set; } = [];
    public string DestinationDirectory { get; set; } = "";
    public List<string> CoverageNotes { get; set; } = [];
    public bool CompleteMigration { get; set; }
    public List<SessionReference> Sessions { get; set; } = [];
    public int DiscoveredSessionCount { get; set; }
    public List<Finding> DiscoveryFindings { get; set; } = [];
    public Dictionary<string,string> PathReplacements { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string SourceCodexVersion { get; set; } = "未知";
    public EnvironmentManifest EnvironmentManifest { get; set; } = new();
    public PreflightReport? Preflight { get; set; }
    /// <summary>Optional in-memory password; never serialized into a manifest or report.</summary>
    public string? EncryptionPassword { get; set; }
}
public sealed record BackupResult(string PackagePath, BackupManifest Manifest);

public sealed class BackupManifest
{
    public int FormatVersion { get; set; } = 1;
    public string ToolVersion { get; set; } = ProductInfo.Version;
    public string BackupId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public string SourceProfile { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public string SourceUser { get; set; } = Environment.UserName;
    public string SourceMachine { get; set; } = Environment.MachineName;
    public List<BackupRoot> Roots { get; set; } = [];
    public List<string> Exclusions { get; set; } = [];
    public List<string> CoverageNotes { get; set; } = [];
    public long FileCount { get; set; }
    public long TotalBytes { get; set; }
    public bool CompleteMigration { get; set; }
    public List<SessionReference> Sessions { get; set; } = [];
    public Dictionary<string,string> PathReplacements { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<SourceItem> LogicalSources { get; set; } = [];
    public string SourceCodexVersion { get; set; } = "未知";
    public EnvironmentManifest EnvironmentManifest { get; set; } = new();
    public PreflightReport? Preflight { get; set; }
}
public sealed class BackupRoot
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string OriginalPath { get; set; } = "";
    public bool IsDirectory { get; set; }
    public SourceKind Kind { get; set; }
    public List<string> SourceIds { get; set; } = [];
}
public sealed class FileRecord
{
    public string Id { get; set; } = "";
    public string RootId { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public bool IsDirectory { get; set; }
    public long Length { get; set; }
    public string Sha256 { get; set; } = "";
    public long LastWriteUtcTicks { get; set; }
    public int Attributes { get; set; }
}
public sealed record VerifiedPackage(string PackagePath, BackupManifest Manifest, IReadOnlyList<FileRecord> Files);
public sealed class RestoreMapping
{
    public string RootId { get; set; } = "";
    public string TargetPath { get; set; } = "";
}
public sealed class RestoreRequest
{
    public string PackagePath { get; set; } = "";
    public List<RestoreMapping> Mappings { get; set; } = [];
    public bool ReplaceExisting { get; set; }
    public bool Isolated { get; set; } = true;
    public bool RequireCompleteMigration { get; set; }
    public string? PrimaryCoreRootId { get; set; }
    public string? TargetCodexVersion { get; set; }
    /// <summary>Optional in-memory password for an encrypted package; never persisted.</summary>
    public string? EncryptionPassword { get; set; }
}
public sealed record RestorePreview(IReadOnlyList<RestorePreviewItem> Items, IReadOnlyList<Finding> Findings, long TotalBytes)
{
    public bool CanProceed => !Findings.Any(f => f.Level == FindingLevel.Blocker);
}
public sealed record RestorePreviewItem(string RootId, string SourceName, string TargetPath, bool Exists, string Action);
public sealed record RestoreResult(string JournalPath, List<string> RestoredPaths, List<string> RollbackPaths, List<string> Notes, RestoreAcceptanceReport? Acceptance = null);
public sealed class BackupException(string message) : Exception(message);
