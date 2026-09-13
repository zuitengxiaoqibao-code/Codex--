namespace CodexBackup.Core;

public enum SourceKind { Core, Project, Memory, Skill, Plugin, Tool, Application, Environment, Custom, Session }
public enum FindingLevel { Info, Warning, Blocker }
public sealed record Finding(FindingLevel Level, string Code, string Message, string? Path = null);
public static class ProductInfo
{
    public const string Version = "0.3.2-preview";
}

public sealed class SourceItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public SourceKind Kind { get; set; }
    public bool Required { get; set; }
    public bool Selected { get; set; } = true;
    public bool Exists { get; set; }
    public bool IsDirectory { get; set; } = true;
    public string Reason { get; set; } = "";
    public string DiscoveredBy { get; set; } = "";
    public string Notes { get; set; } = "";
    public long? EstimatedBytes { get; set; }
    public DateTimeOffset? LastModifiedUtc { get; set; }
    public DateTimeOffset? LastActivityUtc { get; set; }
    public List<string> DependencyIds { get; set; } = [];
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
}

public sealed record OperationProgress(string Phase, string Message, long Files = 0, long Bytes = 0, long? TotalBytes = null);
public sealed class BackupRequest
{
    public List<SourceItem> Sources { get; set; } = [];
    public string DestinationDirectory { get; set; } = "";
    public List<string> CoverageNotes { get; set; } = [];
    public bool CompleteMigration { get; set; }
    public List<SessionReference> Sessions { get; set; } = [];
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
