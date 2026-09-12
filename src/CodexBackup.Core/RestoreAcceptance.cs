using System.Text;

namespace CodexBackup.Core;

public enum RestoreStructuralStatus { Passed, Blocked }
public enum RestoreApplicationStatus { PendingManualCheck }

public sealed record RestoreAcceptanceCheck(string Code, FindingLevel Level, string Message, string? Path = null);

public sealed record DisabledIntegrationEntry(string SourcePath, string RiskClass, string ManualReviewAction);

public sealed class RestoreAcceptanceReport
{
    public RestoreStructuralStatus StructuralStatus { get; init; }
    public RestoreApplicationStatus ApplicationStatus { get; init; } = RestoreApplicationStatus.PendingManualCheck;
    public List<RestoreAcceptanceCheck> Checks { get; init; } = [];
    public List<DisabledIntegrationEntry> DisabledIntegrations { get; init; } = [];
    public bool CanOpenFiles => StructuralStatus == RestoreStructuralStatus.Passed;
    public string Summary => StructuralStatus == RestoreStructuralStatus.Passed
        ? "文件结构验收通过；Codex 登录、侧栏会话、记忆读取和项目运行仍需人工检查。"
        : "文件结构验收未通过；请先处理阻断项，不要清空旧系统。";
}

public static class RestoreAcceptance
{
    private const long PointerLimit = 64 * 1024;

    public static RestoreAcceptanceReport Validate(VerifiedPackage package, RestoreRequest request, IReadOnlyDictionary<string, string> physicalRoots)
    {
        var checks = new List<RestoreAcceptanceCheck>();
        var disabled = new List<DisabledIntegrationEntry>();
        var roots = package.Manifest.Roots ?? [];
        var rootById = roots.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var files = package.Files;
        var mappings = request.Mappings ?? [];

        var duplicateTargets = mappings
            .GroupBy(m => NormalizeSafe(m.TargetPath), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1);
        foreach (var group in duplicateTargets)
            checks.Add(Block("DUPLICATE_TARGET", "多个来源被指向同一个恢复位置，可能互相覆盖；请为每个根目录选择不同目标。", group.Key));

        Dictionary<string, string> pathMappings;
        try
        {
            pathMappings = RestorePlanner.BuildPathMappings(package.Manifest, mappings);
            checks.Add(Pass("PATH_MAPPING", "来源路径与目标路径映射可以解析。"));
        }
        catch (Exception ex) when (ex is BackupException or ArgumentException or InvalidOperationException)
        {
            checks.Add(Block("PATH_MAPPING", "来源路径与目标路径无法一一对应；请重新选择每个根目录的目标位置。", ex.Message));
            pathMappings = new(StringComparer.OrdinalIgnoreCase);
        }

        foreach (var root in roots)
        {
            if (!physicalRoots.TryGetValue(root.Id, out var physical) || string.IsNullOrWhiteSpace(physical))
            {
                checks.Add(Block("ROOT_MISSING", $"恢复后找不到“{root.Name}”的实际位置；相关会话和项目不能确认完整。", root.OriginalPath));
                continue;
            }
            try
            {
                physical = PathSafety.Full(physical);
                if (!Exists(physical)) checks.Add(Block("ROOT_MISSING", $"恢复后找不到“{root.Name}”的实际位置。", physical));
            }
            catch (Exception ex) when (ex is BackupException or ArgumentException)
            { checks.Add(Block("ROOT_INVALID", "恢复后的根目录位置无效。", ex.Message)); }
        }

        foreach (var session in package.Manifest.Sessions ?? [])
        {
            var project = ResolvePhysical(package, session.ProjectPath, true, physicalRoots);
            var transcript = ResolvePhysical(package, session.TranscriptPath, false, physicalRoots);
            if (project is null) checks.Add(Block("PROJECT_MISSING", $"会话 {session.Id} 对应的项目目录不存在；只恢复对话不能恢复项目源码。", session.ProjectPath));
            else checks.Add(Pass("CWD_MAPPED", $"会话 {session.Id} 的工作目录已定位到恢复后的项目。", project));
            if (transcript is null) checks.Add(Block("TRANSCRIPT_MISSING", $"会话 {session.Id} 的对话文件不存在；该会话不能确认可打开。", session.TranscriptPath));
            else checks.Add(Pass("SESSION_PAIR", $"会话 {session.Id} 同时找到项目目录和对话文件。", transcript));
        }

        foreach (var source in package.Manifest.LogicalSources?.Where(s => s.Selected && s.Kind is SourceKind.Project or SourceKind.Memory) ?? [])
        {
            var actual = ResolvePhysical(package, source.Path, source.IsDirectory, physicalRoots);
            if (actual is null) checks.Add(Block(source.Kind == SourceKind.Project ? "PROJECT_MISSING" : "MEMORY_MISSING", $"{source.Name} 的恢复位置不存在。", source.Path));
        }

        ValidateMemoryPointers(package, physicalRoots, pathMappings, checks);
        ValidateGitPointers(package, physicalRoots, checks);

        foreach (var source in package.Manifest.LogicalSources ?? [])
        {
            if (source.Kind is not (SourceKind.Skill or SourceKind.Plugin or SourceKind.Tool or SourceKind.Application or SourceKind.Environment)) continue;
            disabled.Add(new DisabledIntegrationEntry(source.Path, source.Kind switch
            {
                SourceKind.Skill => "技能脚本",
                SourceKind.Plugin => "插件代码",
                SourceKind.Tool => "外围工具配置",
                SourceKind.Application => "应用状态",
                _ => "运行环境信息"
            }, "默认保持停用；先确认来源、版本和权限，再由用户手动启用。"));
        }

        return new RestoreAcceptanceReport
        {
            StructuralStatus = checks.Any(c => c.Level == FindingLevel.Blocker) ? RestoreStructuralStatus.Blocked : RestoreStructuralStatus.Passed,
            ApplicationStatus = RestoreApplicationStatus.PendingManualCheck,
            Checks = checks,
            DisabledIntegrations = disabled
        };
    }

    private static void ValidateMemoryPointers(VerifiedPackage package, IReadOnlyDictionary<string, string> physicalRoots, IReadOnlyDictionary<string, string> pathMappings, List<RestoreAcceptanceCheck> checks)
    {
        foreach (var record in package.Files.Where(f => !f.IsDirectory && Path.GetFileName(f.RelativePath).Equals("obsidian-vault-path.txt", StringComparison.OrdinalIgnoreCase)))
        {
            if (!physicalRoots.TryGetValue(record.RootId, out var root)) continue;
            var path = record.RelativePath.Length == 0 ? root : PathSafety.Under(root, record.RelativePath);
            if (!File.Exists(path)) continue;
            try
            {
                var text = File.ReadAllText(path, Encoding.UTF8);
                var mapped = RestorePlanner.Map(text.Trim(), pathMappings);
                if (Directory.Exists(mapped)) checks.Add(Pass("MEMORY_POINTER", "记忆库指针已指向恢复后的实际目录。", mapped));
                else checks.Add(Block("MEMORY_POINTER", "记忆库指针文件存在，但指向的目录不存在；记忆无法确认可读取。", mapped));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException or BackupException)
            { checks.Add(Block("MEMORY_POINTER", "记忆库指针无法读取或路径无法解析。", path)); }
        }
    }

    private static void ValidateGitPointers(VerifiedPackage package, IReadOnlyDictionary<string, string> physicalRoots, List<RestoreAcceptanceCheck> checks)
    {
        foreach (var record in package.Files.Where(f => !f.IsDirectory && (Path.GetFileName(f.RelativePath).Equals(".git", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f.RelativePath) is "gitdir" or "commondir")))
        {
            if (!physicalRoots.TryGetValue(record.RootId, out var root)) continue;
            var path = record.RelativePath.Length == 0 ? root : PathSafety.Under(root, record.RelativePath);
            if (!File.Exists(path)) { checks.Add(Block("GIT_POINTER", "Git 关联文件在恢复后不存在。", path)); continue; }
            try
            {
                var text = File.ReadAllText(path, Encoding.UTF8);
                if (text.Length > PointerLimit) throw new BackupException("Git 关联文件过大。");
                var value = Path.GetFileName(record.RelativePath).Equals(".git", StringComparison.OrdinalIgnoreCase)
                    ? text.TrimStart().StartsWith("gitdir:", StringComparison.Ordinal) ? text.Trim()[7..].Trim() : throw new BackupException(".git 文件不是受支持的 gitdir 指针。")
                    : text.Trim();
                var target = Path.IsPathFullyQualified(value) ? value : Path.Combine(Path.GetDirectoryName(path)!, value);
                if (Exists(target)) checks.Add(Pass("GIT_POINTER", "Git 关联位置存在。", target));
                else checks.Add(Block("GIT_POINTER", "项目源码存在，但 Git 关联位置不存在；分支和提交状态不能确认。", target));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException or BackupException)
            { checks.Add(Block("GIT_POINTER", "Git 关联文件无法读取或格式不受支持。", path)); }
        }
    }

    private static string? ResolvePhysical(VerifiedPackage package, string path, bool directory, IReadOnlyDictionary<string, string> physicalRoots)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var resolved = RestorePlanner.Resolve(package, path, directory);
            if (!physicalRoots.TryGetValue(resolved.Root.Id, out var physical)) return null;
            var actual = resolved.Record.RelativePath.Length == 0 ? physical : PathSafety.Under(physical, resolved.Record.RelativePath);
            return directory ? Directory.Exists(actual) ? actual : null : File.Exists(actual) ? actual : null;
        }
        catch (BackupException) { return null; }
    }

    private static string NormalizeSafe(string path)
    {
        try { return PathSafety.Full(path); }
        catch { return path.Trim(); }
    }
    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
    private static RestoreAcceptanceCheck Pass(string code, string message, string? path = null) => new(code, FindingLevel.Info, message, path);
    private static RestoreAcceptanceCheck Block(string code, string message, string? path = null) => new(code, FindingLevel.Blocker, message, path);
}
