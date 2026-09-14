using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;

namespace CodexBackup.Core;

public sealed class DiscoveryService
{
    private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;
    private readonly object scanGate = new();
    private readonly List<SourceItem> items = [];
    private readonly List<Finding> findings = [];
    private readonly List<string> packageInstallations = [];
    private readonly List<string> packageVersions = [];
    private readonly List<SessionReference> sessions = [];
    private readonly HashSet<string> scannedCoreRoots = new(Paths);
    private readonly HashSet<string> scannedConfigFiles = new(Paths);
    private string selectedProfile = "";

    public Task<ScanResult> ScanAsync(string? profile = null, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default, IReadOnlyList<string>? additionalRoots = null)
    {
        lock (scanGate)
        {
        cancellationToken.ThrowIfCancellationRequested();
        var current = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        selectedProfile = PathSafety.Full(string.IsNullOrWhiteSpace(profile) ? current : profile);
        var includeProcessEnvironment = Paths.Equals(selectedProfile, current);
        var result = new ScanResult { UserProfile = selectedProfile };
        items.Clear(); findings.Clear(); packageInstallations.Clear(); packageVersions.Clear(); sessions.Clear(); scannedCoreRoots.Clear(); scannedConfigFiles.Clear();

        progress?.Report(new("检测", "正在检查 Codex 数据位置和引用"));
        var roots = new List<(string Path, string Evidence)> { (Path.Combine(selectedProfile, ".codex"), "用户默认目录") };
        if (includeProcessEnvironment)
        {
            var envHome = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (!string.IsNullOrWhiteSpace(envHome)) roots.Add((envHome, "CODEX_HOME 环境变量"));
            foreach (var scope in new[] { EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
            {
                var configured = Environment.GetEnvironmentVariable("CODEX_HOME", scope);
                if (!string.IsNullOrWhiteSpace(configured)) roots.Add((configured, scope == EnvironmentVariableTarget.User ? "用户级 CODEX_HOME 环境变量" : "系统级 CODEX_HOME 环境变量"));
            }
        }

        var defaultCorePath = Normalize(Path.Combine(selectedProfile, ".codex"));
        var normalizedRoots = new List<(string Path, string Evidence)>();
        foreach (var root in roots)
        {
            try { normalizedRoots.Add((Normalize(root.Path), root.Evidence)); }
            catch (Exception ex) { findings.Add(new(FindingLevel.Warning, "configured-core-invalid", $"配置的 Codex 数据目录无法识别（{ex.GetType().Name}）；已跳过该位置，请改成明确的本地磁盘路径。", root.Path)); }
        }
        foreach (var root in normalizedRoots.DistinctBy(x => x.Path, Paths))
            ScanCoreRoot(root.Path, root.Evidence, cancellationToken, !root.Evidence.Equals("用户默认目录", StringComparison.Ordinal));
        foreach (var root in additionalRoots ?? []) ScanAdditionalRoot(root, cancellationToken);
        FinalizeCoreRequirement(defaultCorePath);

        if (includeProcessEnvironment)
        {
            foreach (var scope in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
            {
                var sqliteHome = Environment.GetEnvironmentVariable("CODEX_SQLITE_HOME", scope);
                if (!string.IsNullOrWhiteSpace(sqliteHome)) ScanSqliteLocation(sqliteHome, $"{scope} 级 CODEX_SQLITE_HOME 环境变量", cancellationToken, true);
            }
        }
        else
        {
            var machineSqliteHome = Environment.GetEnvironmentVariable("CODEX_SQLITE_HOME", EnvironmentVariableTarget.Machine);
            if (!string.IsNullOrWhiteSpace(machineSqliteHome)) ScanSqliteLocation(machineSqliteHome, "系统级 CODEX_SQLITE_HOME 环境变量", cancellationToken, true);
        }
        // ProgramData is machine-wide and applies even when the scan targets another user profile.
        ScanSystemConfigLocations(cancellationToken);

        ScanProfileLocations(selectedProfile, cancellationToken);
        if (includeProcessEnvironment)
        {
            var activeCorePath = items.FirstOrDefault(x => x.Kind == SourceKind.Core && x.Exists)?.Path ?? defaultCorePath;
            ScanProjectConfig(Environment.CurrentDirectory, activeCorePath, cancellationToken);
        }
        if (includeProcessEnvironment)
        {
            var installedPackages = WindowsEnvironment.GetInstalledCodexMsixPackages(cancellationToken, out var packageWarning);
            foreach (var package in installedPackages)
            {
                packageInstallations.Add(package.InstallLocation);
                if (!string.IsNullOrWhiteSpace(package.Version)) packageVersions.Add(package.Version);
            }
            if (packageWarning is not null) findings.Add(new(FindingLevel.Warning, "msix-query-incomplete", packageWarning));
        }
        foreach (var location in WindowsEnvironment.GetInstalledLocations(selectedProfile))
        {
            if (Directory.Exists(location) || File.Exists(location)) result.InstallationPaths.Add(location);
        }
        result.InstallationPaths.AddRange(packageInstallations.Where(x => !result.InstallationPaths.Contains(x, Paths)));
        result.CodexVersion = packageVersions.FirstOrDefault() ?? DiscoverVersion(scannedCoreRoots);
        if (result.CodexVersion == "未知") findings.Add(new(FindingLevel.Info, "codex-version-unknown", "未能从已知版本文件确认 Codex 版本；不会执行未知程序来探测。"));
        foreach (var missing in items.Where(x => !x.Exists && !x.Required))
            findings.Add(new(FindingLevel.Info, "known-location-missing", "此用户没有该可选位置，默认不选择。", missing.Path));
        var writers = includeProcessEnvironment ? WindowsEnvironment.GetWriterNames() : [];
        if (writers.Count > 0) findings.Add(new(FindingLevel.Warning, "active-writers", $"相关程序正在运行，最终备份前必须退出：{string.Join(", ", writers)}"));
        findings.Add(new(FindingLevel.Warning, "coverage-boundary", "当前仅覆盖已知位置和解析出的引用；外部数据库、WSL/Docker、网络位置、其他用户和未知版本字段需另行核查。"));

        result.Items = [.. items];
        result.Findings = [.. findings];
        result.Sessions = [.. sessions];
        result.EnvironmentManifest = EnvironmentInventory.Collect(selectedProfile, result.Items, cancellationToken, includeProcessEnvironment, result.Findings);
        progress?.Report(new("检测", $"发现 {items.Count} 项来源"));
        return Task.FromResult(result);
        }
    }

    private void ScanCoreRoot(string path, string evidence, CancellationToken token, bool explicitlyRequired = true)
    {
        token.ThrowIfCancellationRequested();
        string full;
        try { full = ResolveConfiguredPath(path, selectedProfile); }
        catch (Exception ex) { findings.Add(new(FindingLevel.Blocker, "configured-core-invalid", $"配置的 Codex 数据目录无效（{ex.GetType().Name}）。", path)); return; }
        if (!scannedCoreRoots.Add(full))
        {
            var prior = items.FirstOrDefault(x => x.Kind == SourceKind.Core && Paths.Equals(x.Path, full));
            if (explicitlyRequired && prior is not null && !prior.Required)
            {
                prior.Required = true; prior.Selected = true;
                if (!prior.Exists) findings.Add(new(FindingLevel.Blocker, "required-codex-root-missing", "显式配置的 Codex 数据目录不存在或不可访问，请核对配置。", full));
            }
            return;
        }
        if (scannedCoreRoots.Count > 32) { findings.Add(new(FindingLevel.Blocker, "configured-core-limit", "配置引用的 Codex 数据目录超过 32 个，后续目录未检测。", full)); return; }
        var core = Add("Codex 会话主数据（会话索引、正文与个人设置）", full, SourceKind.Core, explicitlyRequired, evidence);
        if (!core.Exists)
        {
            if (explicitlyRequired) findings.Add(new(FindingLevel.Blocker, "required-codex-root-missing", "显式配置的 Codex 数据目录不存在或不可访问，请核对配置。", core.Path));
            return;
        }
        ScanCodexRoot(core.Path, token);
    }

    private void FinalizeCoreRequirement(string defaultCorePath)
    {
        var cores = items.Where(x => x.Kind == SourceKind.Core).ToList();
        var existing = cores.Where(x => x.Exists).ToList();
        foreach (var core in existing) { core.Required = true; core.Selected = true; core.Reason = "Codex 会话主数据，完整迁移必须保留；可重建的顶层日志、缓存、临时运行状态和登录令牌会自动排除。"; }
        if (existing.Count > 0) return;
        var fallback = cores.First(x => Paths.Equals(x.Path, defaultCorePath));
        fallback.Required = true; fallback.Selected = true; fallback.Reason = "未发现其他有效 Codex 数据目录，默认目录必须核查。";
        if (!findings.Any(x => x.Code == "required-codex-root-missing" && Paths.Equals(x.Path, fallback.Path)))
            findings.Add(new(FindingLevel.Blocker, "required-codex-root-missing", "未发现可用的 Codex 数据目录；默认目录也不存在或不可访问。", fallback.Path));
    }

    private void ScanAdditionalRoot(string path, CancellationToken token)
    {
        string full;
        try { full = ResolveConfiguredPath(path, selectedProfile); }
        catch (Exception ex) { findings.Add(new(FindingLevel.Blocker, "additional-root-invalid", $"用户选择的附加检测目录无效（{ex.GetType().Name}）。", path)); return; }
        if (!Directory.Exists(full)) { findings.Add(new(FindingLevel.Blocker, "additional-root-missing", "用户指定的附加检测目录不存在或不可访问。", full)); return; }
        ScanAdditionalSearchTree(full, token);
    }

    private void ScanAdditionalSearchTree(string root, CancellationToken token)
    {
        var queue = new Queue<(string Path, int Depth)>(); queue.Enqueue((root, 0)); var visited = 0; var discoveries = 0;
        while (queue.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var (current, depth) = queue.Dequeue();
            if (++visited > 5000) { findings.Add(new(FindingLevel.Warning, "additional-search-limit", "附加目录检测超过五千个目录，后续内容未检测。", root)); break; }
            if (LooksLikeCoreRoot(current)) { ScanCoreRoot(current, "用户附加目录中发现的 Codex 数据目录", token); discoveries++; continue; }
            var dotGit = Path.Combine(current, ".git");
            if (Directory.Exists(dotGit) || File.Exists(dotGit))
            {
                var project = Add(Path.GetFileName(current), current, SourceKind.Project, false, "用户附加目录中发现的 Git 项目");
                AddGitDependencies(project);
                ScanProjectConfig(current, scannedCoreRoots.FirstOrDefault() ?? Path.Combine(selectedProfile, ".codex"), token);
                discoveries++; continue;
            }
            try
            {
                if (Directory.EnumerateFiles(current, "*.jsonl").Any()) { ScanSessionTree(current, scannedCoreRoots.FirstOrDefault() ?? current, token, InferSessionLifecycle(current)); discoveries++; continue; }
                if (depth >= 5)
                {
                    if (Directory.EnumerateDirectories(current).Any()) findings.Add(new(FindingLevel.Warning, "additional-depth-limit", "附加目录检测超过五层，深层内容未检测。", current));
                    continue;
                }
                var children = Directory.EnumerateDirectories(current).Take(1001).ToList();
                if (children.Count > 1000) findings.Add(new(FindingLevel.Warning, "additional-child-limit", "附加搜索目录单层超过一千项，超出部分未检测。", current));
                foreach (var child in children.Take(1000)) queue.Enqueue((child, depth + 1));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { findings.Add(new(FindingLevel.Warning, "additional-search-incomplete", $"无法读取附加搜索目录（{ex.GetType().Name}）。", current)); }
        }
        if (discoveries == 0) findings.Add(new(FindingLevel.Warning, "additional-search-empty", "附加目录中未发现已知 Codex 数据、会话或 Git 项目；不会自动把整个搜索容器加入备份。", root));
    }

    private void ScanCodexRoot(string root, CancellationToken token)
    {
        if (!Directory.Exists(root)) return;
        AddChildren(Path.Combine(root, "skills"), SourceKind.Skill, "Codex 技能目录");
        AddChildren(Path.Combine(root, "plugins"), SourceKind.Plugin, "Codex 插件目录");
        AddChildren(Path.Combine(root, "marketplaces"), SourceKind.Plugin, "Codex 插件市场目录");
        var history = Path.Combine(root, "history.jsonl");
        if (File.Exists(history)) Add("Codex 历史记录", history, SourceKind.Session, true, "官方固定路径：CODEX_HOME/history.jsonl");
        foreach (var name in new[] { "state_5.sqlite", "logs_2.sqlite", "goals_1.sqlite", "memories_1.sqlite", "memories_v2_1.sqlite", "queue_1.sqlite", "thread_history_1.sqlite" })
        {
            var database = Path.Combine(root, name);
            if (File.Exists(database)) Add("Codex 运行时数据库 " + name, database, SourceKind.Environment, true, "官方运行时状态数据库");
        }
        ScanManagedWorktrees(Path.Combine(root, "worktrees"), token);
        foreach (var directory in new[] { "sessions", "archived_sessions", "archived", "custom_sessions", "custom" })
            ScanSessionTree(Path.Combine(root, directory), root, token, InferSessionLifecycle(directory));

        var pointer = Path.Combine(root, "memories", "obsidian-vault-path.txt");
        if (File.Exists(pointer))
        {
            try
            {
                if (new FileInfo(pointer).Length > 64 * 1024) throw new IOException("指针文件超出读取上限");
                var target = File.ReadAllText(pointer).Trim();
                if (string.IsNullOrWhiteSpace(target)) findings.Add(new(FindingLevel.Warning, "empty-memory-pointer", "记忆库指针为空，无法确认外部记忆库。", pointer));
                else
                {
                    var memory = Add("外部记忆库", target, SourceKind.Memory, true, $"记忆指针：{pointer}");
                    if (!memory.Exists) findings.Add(new(FindingLevel.Blocker, "referenced-memory-missing", "记忆库指针的目标不存在或不可访问。", memory.Path));
                }
            }
            catch (Exception ex) { findings.Add(new(FindingLevel.Warning, "memory-pointer-unreadable", $"无法读取记忆库指针（{ex.GetType().Name}）。", pointer)); }
        }

        foreach (var file in EnumerateTopFiles(root, ["*.json", "*.jsonl"]))
        {
            token.ThrowIfCancellationRequested();
            var name = Path.GetFileName(file);
            if (name.Contains("config", StringComparison.OrdinalIgnoreCase) || name.Contains("project", StringComparison.OrdinalIgnoreCase) || name.Contains("global-state", StringComparison.OrdinalIgnoreCase)) ScanJson(file, token);
        }
        foreach (var file in EnumerateTopFiles(root, ["*.sqlite", "*.db"])) ScanSqlite(file, root, token);
        if (Directory.Exists(Path.Combine(root, "sqlite")))
            foreach (var file in EnumerateTopFiles(Path.Combine(root, "sqlite"), ["*.db", "*.sqlite"])) ScanSqlite(file, root, token);
        ScanConfigReferences(root, root, token);
    }

    private void ScanSystemConfigLocations(CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) return;
        var paths = WindowsEnvironment.GetCodexSystemConfigPaths();
        var systemRoot = Path.GetDirectoryName(paths.ConfigPath)!;
        var found = false;
        foreach (var (path, name, description) in new[]
        {
            (paths.ConfigPath, "系统级 Codex 默认配置", "官方配置层：%ProgramData%\\OpenAI\\Codex\\config.toml"),
            (paths.RequirementsPath, "系统级 Codex 强制要求", "官方受管配置层：%ProgramData%\\OpenAI\\Codex\\requirements.toml")
        })
        {
            token.ThrowIfCancellationRequested();
            if (!File.Exists(path)) continue;
            found = true;
            Add(name, path, SourceKind.Environment, true, description);
        }
        if (found) ScanConfigReferences(systemRoot, Path.Combine(selectedProfile, ".codex"), token);
    }

    private void ScanSqliteLocation(string path, string evidence, CancellationToken token, bool required)
    {
        string full;
        try { full = ResolveConfiguredPath(path, selectedProfile); }
        catch (Exception ex) { findings.Add(new(FindingLevel.Blocker, "configured-sqlite-home-invalid", $"Codex SQLite 状态目录配置无效（{ex.GetType().Name}）。", path)); return; }
        var item = Add("Codex SQLite 状态目录", full, SourceKind.Environment, required, evidence);
        if (!item.Exists)
        {
            findings.Add(new(required ? FindingLevel.Blocker : FindingLevel.Warning, "configured-sqlite-home-missing", "Codex 配置指定的 SQLite 状态目录不存在或不可访问。", full));
            return;
        }
        if (!item.IsDirectory)
        {
            findings.Add(new(FindingLevel.Blocker, "configured-sqlite-home-not-directory", "Codex SQLite 状态位置必须是目录。", full));
            return;
        }
        var sessionCore = items.FirstOrDefault(x => x.Kind == SourceKind.Core && x.Exists)?.Path ?? Path.Combine(selectedProfile, ".codex");
        foreach (var file in EnumerateTopFiles(full, ["*.sqlite", "*.db"])) ScanSqlite(file, sessionCore, token);
    }

    private void ScanProfileLocations(string profile, CancellationToken token)
    {
        foreach (var name in new[] { ".cc-connect", ".cc-switch" }) Add(name, Path.Combine(profile, name), SourceKind.Tool, Directory.Exists(Path.Combine(profile, name)), "已知外围工具配置");
        foreach (var suffix in new[] { Path.Combine("AppData", "Roaming", "Codex"), Path.Combine("AppData", "Roaming", "Codex++"), Path.Combine("AppData", "Local", "Codex"), Path.Combine("AppData", "Local", "Codex++") })
            Add(Path.GetFileName(suffix), Path.Combine(profile, suffix), SourceKind.Application, false, "已知应用数据位置");

        var packages = Path.Combine(profile, "AppData", "Local", "Packages");
        if (Directory.Exists(packages))
        {
            try
            {
                foreach (var package in Directory.EnumerateDirectories(packages).Where(x => Path.GetFileName(x).Contains("Codex", StringComparison.OrdinalIgnoreCase)))
                {
                    token.ThrowIfCancellationRequested();
                    Add("商店版 Codex 应用状态", Path.Combine(package, "LocalCache", "Roaming", "Codex"), SourceKind.Application, false, "MSIX 重定向目录");
                    foreach (var folder in new[] { (Path: "LocalState", Name: "本地状态"), (Path: "RoamingState", Name: "漫游状态"), (Path: "Settings", Name: "设置") })
                        Add("商店版" + folder.Name, Path.Combine(package, folder.Path), SourceKind.Application, false, "MSIX 应用状态目录");
                    ReadMsixManifest(package);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { findings.Add(new(FindingLevel.Warning, "msix-scan-incomplete", $"商店应用包检测不完整（MSIX，{ex.GetType().Name}）。", packages)); }
        }
        var ordinary = Add("普通任务工作文件", Path.Combine(profile, "Documents", "Codex"), SourceKind.Project, false, "Codex 普通任务默认位置");
        if (ordinary.Exists && ordinary.IsDirectory)
        {
            AddGitDependencies(ordinary);
            ScanProjectConfig(ordinary.Path, items.FirstOrDefault(x => x.Kind == SourceKind.Core && x.Exists)?.Path ?? Path.Combine(profile, ".codex"), token);
        }
        if (Paths.Equals(profile, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)))
        {
            var redirected = Add("已重定向的普通任务工作文件", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Codex"), SourceKind.Project, false, "Windows 已知文件夹配置");
            if (redirected.Exists && redirected.IsDirectory)
            {
                AddGitDependencies(redirected);
                ScanProjectConfig(redirected.Path, items.FirstOrDefault(x => x.Kind == SourceKind.Core && x.Exists)?.Path ?? Path.Combine(profile, ".codex"), token);
            }
        }
    }

    private void AddChildren(string parent, SourceKind kind, string evidence)
    {
        if (!Directory.Exists(parent)) return;
        try { foreach (var child in Directory.EnumerateDirectories(parent)) Add(Path.GetFileName(child), child, kind, true, evidence); }
        catch (Exception ex) { findings.Add(new(FindingLevel.Warning, "subentry-scan-incomplete", $"无法完整列出子项（{ex.GetType().Name}）。", parent)); }
    }

    private void ScanManagedWorktrees(string root, CancellationToken token)
    {
        if (!Directory.Exists(root)) return;
        var pending = new Queue<(string Path, int Depth)>(); pending.Enqueue((root, 0));
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var (current, depth) = pending.Dequeue();
            if (depth > 0 && (File.Exists(Path.Combine(current, ".git")) || Directory.Exists(Path.Combine(current, ".git"))))
            {
                var worktree = Add(Path.GetFileName(current), current, SourceKind.Project, true, "Codex 管理的工作树目录");
                AddGitDependencies(worktree);
                continue;
            }
            if (depth >= 3) continue;
            try
            {
                var children = Directory.EnumerateDirectories(current).Take(1001).ToList();
                if (children.Count > 1000) findings.Add(new(FindingLevel.Warning, "worktree-directory-limit", "单层工作树目录超过一千项，超出部分未检测。", current));
                foreach (var child in children.Take(1000)) pending.Enqueue((child, depth + 1));
            }
            catch (Exception ex) { findings.Add(new(FindingLevel.Warning, "worktree-scan-incomplete", $"无法列出 Codex 工作树目录（{ex.GetType().Name}）。", current)); }
            if (pending.Count > 3000) { findings.Add(new(FindingLevel.Warning, "worktree-limit", "工作树目录超过有界检测上限，部分目录未检测。", root)); break; }
        }
    }

    private void ReadMsixManifest(string package)
    {
        var manifest = Path.Combine(package, "AppxManifest.xml");
        packageInstallations.Add(Path.GetFullPath(package));
        if (!File.Exists(manifest)) { findings.Add(new(FindingLevel.Info, "msix-version-unknown", "已找到商店应用数据，但没有可读的 AppxManifest.xml，无法确认版本。", package)); return; }
        try
        {
            if (new FileInfo(manifest).Length > 1024 * 1024) throw new IOException("manifest 超出读取上限");
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 };
            using var reader = XmlReader.Create(manifest, settings);
            var identity = XDocument.Load(reader).Descendants().FirstOrDefault(x => x.Name.LocalName == "Identity");
            var version = identity?.Attribute("Version")?.Value;
            if (!string.IsNullOrWhiteSpace(version)) packageVersions.Add(version);
            else findings.Add(new(FindingLevel.Info, "msix-version-unknown", "商店应用清单中没有可识别的版本（AppxManifest.xml）。", manifest));
        }
        catch (Exception ex) { findings.Add(new(FindingLevel.Warning, "msix-manifest-unreadable", $"无法读取 MSIX 清单（{ex.GetType().Name}）。", manifest)); }
    }

    private void ScanJson(string file, CancellationToken token)
    {
        try
        {
            if (new FileInfo(file).Length > 16 * 1024 * 1024) throw new IOException("元数据超出读取上限");
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            WalkJson(document.RootElement, null, file, null, token);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { findings.Add(new(FindingLevel.Warning, "metadata-json-unreadable", $"无法解析 JSON 元数据（{ex.GetType().Name}）。", file)); }
    }

    private void WalkJson(JsonElement element, string? property, string evidence, DateTimeOffset? inheritedActivity, CancellationToken token)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var activity = inheritedActivity;
            foreach (var member in element.EnumerateObject())
                if (IsActivityColumn(member.Name) && TryParseActivity(member.Value, out var parsed)) activity = parsed;
            foreach (var member in element.EnumerateObject())
            {
                if (IsPathKeyMap(property) && Path.IsPathFullyQualified(member.Name)) AddReferencedProject(member.Name, evidence, activity);
                WalkJson(member.Value, IsPathValueMap(property) ? property : member.Name, evidence, activity, token);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) WalkJson(child, property, evidence, inheritedActivity, token);
        else if (element.ValueKind == JsonValueKind.String && (IsPathColumn(property) || IsPathValueMap(property))) AddReferencedProject(element.GetString(), evidence, inheritedActivity, token);
    }

    private void ScanSqlite(string file, string corePath, CancellationToken token)
    {
        try
        {
            SQLitePCL.Batteries_V2.Init();
            var uri = new Uri(file).AbsoluteUri + "?immutable=1";
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = uri, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            connection.Open();
            var tables = new List<string>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA trusted_schema=OFF; SELECT name FROM sqlite_master WHERE type='table' AND (name LIKE '%project%' OR name LIKE '%workspace%' OR name IN ('threads','tasks')) LIMIT 100";
                using var reader = command.ExecuteReader(); while (reader.Read()) tables.Add(reader.GetString(0));
            }
            foreach (var table in tables)
            {
                token.ThrowIfCancellationRequested();
                var allColumns = GetColumns(connection, table).ToList();
                if (table.Equals("threads", StringComparison.OrdinalIgnoreCase)) ScanSqliteSessions(connection, table, allColumns, file, corePath, token);
                var activityColumn = allColumns.FirstOrDefault(IsActivityColumn);
                foreach (var column in allColumns.Where(IsPathColumn))
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = $"SELECT {Quote(column)}{(activityColumn is null ? "" : ", " + Quote(activityColumn))} FROM {Quote(table)} WHERE {Quote(column)} IS NOT NULL LIMIT 10001";
                    using var reader = command.ExecuteReader(); var count = 0;
                    while (reader.Read())
                    {
                        token.ThrowIfCancellationRequested();
                        if (++count > 10000) { findings.Add(new(FindingLevel.Warning, "reference-limit", "引用数量超过一万，后续引用未纳入检测。", file)); break; }
                        DateTimeOffset? activity = null;
                        if (activityColumn is not null && !reader.IsDBNull(1) && TryParseActivity(reader.GetValue(1), out var parsed)) activity = parsed;
                        AddReferencedProject(reader.GetValue(0)?.ToString(), $"SQLite 元数据 {Path.GetFileName(file)}:{table}.{column}", activity, token);
                    }
                }
            }
            if (File.Exists(file + "-wal") && new FileInfo(file + "-wal").Length > 0) findings.Add(new(FindingLevel.Warning, "sqlite-wal-coverage-uncertain", "数据库有 WAL；只读检测可能缺少尚未写回的项目引用。关闭 Codex 后请重新检测。", file));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { findings.Add(new(FindingLevel.Warning, "sqlite-coverage-unknown", $"无法以只读方式检查 SQLite 项目元数据（{ex.GetType().Name}）。", file)); }
    }

    private static IEnumerable<string> GetColumns(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand(); command.CommandText = $"PRAGMA table_info({Quote(table)})";
        using var reader = command.ExecuteReader(); while (reader.Read()) yield return reader.GetString(1);
    }

    private void ScanSqliteSessions(SqliteConnection connection, string table, IReadOnlyList<string> columns, string database, string corePath, CancellationToken token)
    {
        if (!columns.Contains("rollout_path", StringComparer.OrdinalIgnoreCase)) return;
        string? Pick(params string[] names) => names.FirstOrDefault(n => columns.Contains(n, StringComparer.OrdinalIgnoreCase));
        var id = Pick("id", "thread_id"); var title = Pick("title", "name"); var cwd = Pick("cwd", "project_path", "workspace_root");
        var activity = Pick("updated_at_ms", "recency_at_ms", "updated_at", "recency_at", "created_at_ms", "created_at");
        static string Select(string? column, string alias) => column is null ? $"NULL AS {alias}" : $"{Quote(column)} AS {alias}";
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Select(id, "sid")}, {Select(title, "stitle")}, {Select(cwd, "scwd")}, {Quote("rollout_path")} AS srollout, {Select(activity, "sactivity")} FROM {Quote(table)} WHERE {Quote("rollout_path")} IS NOT NULL LIMIT 10001";
        using var reader = command.ExecuteReader(); var count = 0;
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (++count > 10000) { findings.Add(new(FindingLevel.Warning, "session-reference-limit", "SQLite 会话引用超过一万条，后续记录未检测。", database)); break; }
            DateTimeOffset? timestamp = null;
            if (!reader.IsDBNull(4) && TryParseActivity(reader.GetValue(4), out var parsed)) timestamp = parsed;
            AddSession(reader.IsDBNull(0) ? "" : reader.GetValue(0)?.ToString(), reader.IsDBNull(1) ? "" : reader.GetValue(1)?.ToString(), corePath,
                reader.IsDBNull(2) ? null : reader.GetValue(2)?.ToString(), reader.IsDBNull(3) ? null : reader.GetValue(3)?.ToString(), timestamp,
                $"SQLite 会话元数据：{Path.GetFileName(database)}", token);
        }
    }

    private void ScanConfigReferences(string root, string corePath, CancellationToken token)
    {
        var files = new List<string>();
        foreach (var name in new[] { "config.toml", "requirements.toml", "managed_config.toml", "config.yaml", "config.yml" })
        {
            var file = Path.Combine(root, name);
            if (File.Exists(file)) files.Add(file);
        }
        try
        {
            files.AddRange(Directory.EnumerateFiles(root, "*.config.toml", SearchOption.TopDirectoryOnly));
        }
        catch (Exception ex)
        {
            findings.Add(new(FindingLevel.Warning, "config-profile-list-incomplete", $"无法列出 Codex 配置档（{ex.GetType().Name}）。", root));
        }
        foreach (var file in files.Distinct(Paths)) ScanConfigFile(file, corePath, token);
    }

    private void ScanProjectConfig(string projectPath, string corePath, CancellationToken token)
    {
        string current;
        try { current = Normalize(projectPath); }
        catch { return; }
        if (!Directory.Exists(current)) return;
        var files = new List<string> { Path.Combine(current, "config.toml") };
        var hostProfile = Normalize(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var scanningAnotherProfile = !Paths.Equals(selectedProfile, hostProfile);
        var depth = 0;
        var reachedDepthLimit = false;
        for (; depth <= 32; depth++)
        {
            if (scanningAnotherProfile && Paths.Equals(current, hostProfile)) break;
            files.Add(Path.Combine(current, ".codex", "config.toml"));
            var parent = Directory.GetParent(current)?.FullName;
            if (string.IsNullOrWhiteSpace(parent) || Paths.Equals(parent, current)) break;
            current = parent;
        }
        if (depth > 32)
            reachedDepthLimit = true;
        if (reachedDepthLimit)
            findings.Add(new(FindingLevel.Warning, "project-config-depth-limit", "项目配置向上追踪超过 32 层，深层父目录配置未检测；请把项目根目录或需要的配置目录作为附加位置重新扫描。", current));
        foreach (var file in files.Distinct(Paths))
            if (File.Exists(file)) ScanConfigFile(file, corePath, token);
    }

    private void ScanConfigFile(string file, string corePath, CancellationToken token)
    {
        string full;
        try { full = Normalize(file); }
        catch { return; }
        if (!scannedConfigFiles.Add(full)) return;
        try
        {
            if (new FileInfo(full).Length > 4 * 1024 * 1024) throw new IOException("配置文件超出读取上限");
            var fileName = Path.GetFileName(full);
            if (fileName.EndsWith(".config.toml", StringComparison.OrdinalIgnoreCase))
                Add("Codex 配置档 " + fileName[..^".config.toml".Length], full, SourceKind.Environment, true, "官方用户配置档：CODEX_HOME/<name>.config.toml");
            var sectionName = "";
            var sectionPath = "";
            void RegisterSkill(string raw, string evidence)
            {
                if (!LooksLikeLocalPathLiteral(raw)) return;
                var configured = ResolveConfiguredPath(UnescapeConfig(raw), Path.GetDirectoryName(full)!);
                var skill = Add("Codex 配置的技能文件", configured, SourceKind.Skill, true, evidence);
                if (!skill.Exists) findings.Add(new(FindingLevel.Warning, "configured-skill-missing", "配置引用的技能文件不存在或不可访问；恢复后需要重新安装或定位。", configured));
            }
            void RegisterExternalPath(string raw, string evidence, string description)
            {
                if (LooksLikeRemoteSource(raw)) return;
                var configured = ResolveConfiguredPath(UnescapeConfig(raw), Path.GetDirectoryName(full)!);
                var external = Add(description, configured, SourceKind.Environment, true, evidence);
                if (!external.Exists) findings.Add(new(FindingLevel.Warning, "configured-external-path-missing", "Codex 配置引用的外部文件或目录不存在；恢复后需要重新定位。", configured));
            }
            var toml = ReadTomlStatements(full, 100000);
            if (toml.Truncated) findings.Add(new(FindingLevel.Warning, "config-line-limit", "配置文件行数超过十万，后续内容未检测。", full));
            var localMarketplaceSections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var prepassSection = "";
            foreach (var statement in toml.Statements)
            {
                var parsedSection = ExtractSectionPath(statement);
                if (parsedSection.Length > 0) prepassSection = parsedSection;
                var sourceType = Regex.Match(statement, "(?is)^\\s*source_type\\s*=\\s*(?<q>['\"])(?<value>.*?)\\k<q>");
                if (sourceType.Success && prepassSection.StartsWith("marketplaces.", StringComparison.OrdinalIgnoreCase) && sourceType.Groups["value"].Value.Equals("local", StringComparison.OrdinalIgnoreCase))
                    localMarketplaceSections.Add(prepassSection);
            }
            foreach (var statement in toml.Statements)
            {
                token.ThrowIfCancellationRequested();
                var section = Regex.Match(statement, "(?i)^\\s*\\[\\s*projects\\s*\\.\\s*(?<q>['\"])(?<path>.*?)\\k<q>\\s*\\]\\s*$");
                if (section.Success)
                {
                    try { AddReferencedProject(ResolveConfiguredPath(UnescapeConfig(section.Groups["path"].Value), Path.GetDirectoryName(full)!), $"TOML 项目节：{full}", null, token, corePath); }
                    catch (Exception ex) { findings.Add(new(FindingLevel.Warning, "project-path-invalid", $"项目路径格式无效，无法纳入检测（{ex.GetType().Name}）。", full)); }
                    continue;
                }
                foreach (Match external in Regex.Matches(statement, "(?is)(?<key>config_file|ca_certificate|client_certificate|client_private_key|managed_dir|windows_managed_dir)\\s*=\\s*(?<q>['\"])(?<path>.*?)\\k<q>"))
                {
                    var externalKey = external.Groups["key"].Value.ToLowerInvariant();
                    var isAgent = externalKey == "config_file" && (sectionPath.StartsWith("agents.", StringComparison.OrdinalIgnoreCase) || statement.Contains("agents", StringComparison.OrdinalIgnoreCase));
                    var isOtel = externalKey is "ca_certificate" or "client_certificate" or "client_private_key" && (sectionPath.StartsWith("otel", StringComparison.OrdinalIgnoreCase) || statement.Contains("otel", StringComparison.OrdinalIgnoreCase));
                    var isHook = externalKey is "managed_dir" or "windows_managed_dir" && (sectionPath.StartsWith("hooks", StringComparison.OrdinalIgnoreCase) || statement.Contains("hooks", StringComparison.OrdinalIgnoreCase));
                    if (isAgent || isOtel || isHook)
                    {
                        try { RegisterExternalPath(external.Groups["path"].Value, $"Codex 配置路径引用：{full}", "Codex 配置引用的外部文件"); }
                        catch (Exception ex) { findings.Add(new(FindingLevel.Warning, "config-path-invalid", $"配置中的路径无法解析（{ex.GetType().Name}）。", full)); }
                    }
                }
                var namedSection = Regex.Match(statement, "(?i)^\\s*\\[\\[?\\s*(?<name>[a-z][a-z0-9_-]*)(?:\\s*\\.\\s*(?<q>['\"])(?<path>.*?)\\k<q>)?.*\\]\\]?\\s*$");
                if (namedSection.Success)
                {
                    sectionName = namedSection.Groups["name"].Value.ToLowerInvariant();
                    sectionPath = ExtractSectionPath(statement);
                    if (sectionName == "skills" && namedSection.Groups["path"].Success)
                        try { RegisterSkill(namedSection.Groups["path"].Value, $"Codex 技能配置：{full}"); }
                        catch (Exception ex) { findings.Add(new(FindingLevel.Warning, "configured-skill-invalid", $"技能路径无法解析（{ex.GetType().Name}）。", full)); }
                    continue;
                }
                var quotedKey = Regex.Match(statement, "(?s)^\\s*(?<q>['\"])(?<path>.*?)\\k<q>\\s*=");
                if (quotedKey.Success && sectionName == "skills")
                {
                    try { RegisterSkill(quotedKey.Groups["path"].Value, $"Codex 技能配置：{full}"); }
                    catch (Exception ex) { findings.Add(new(FindingLevel.Warning, "configured-skill-invalid", $"技能路径无法解析（{ex.GetType().Name}）。", full)); }
                    continue;
                }
                var assignment = Regex.Match(statement, "(?is)^\\s*(?<key>[a-z][a-z0-9_-]*(?:\\s*\\.\\s*[a-z][a-z0-9_-]*)*)\\s*[=:]\\s*(?<value>.+?)\\s*$");
                if (!assignment.Success) continue;
                var keyPath = assignment.Groups["key"].Value.Replace(" ", "", StringComparison.Ordinal).Replace('-', '_').ToLowerInvariant();
                var key = keyPath.Split('.').Last();
                var kind = key switch
                {
                    "codex_home" or "data_dir" or "data_root" or "codex_dir" => "core",
                    "sqlite_home" => "sqlite",
                    "log_dir" => "log",
                    "model_instructions_file" or "model_catalog_json" or "experimental_compact_prompt_file" or "js_repl_node_path" or "js_repl_node_module_dirs" => "environment",
                    "sessions_dir" or "session_dir" or "sessions_root" or "session_root" => "session",
                    "project_path" or "project_root" or "workspace" or "workspace_path" or "workspace_root" or "cwd" => "project",
                    "path" when sectionName == "skills" => "skill",
                    "config_file" when sectionPath.StartsWith("agents.", StringComparison.OrdinalIgnoreCase) || keyPath.StartsWith("agents.", StringComparison.OrdinalIgnoreCase) => "environment",
                    "ca_certificate" or "client_certificate" or "client_private_key" when sectionPath.StartsWith("otel", StringComparison.OrdinalIgnoreCase) || keyPath.StartsWith("otel.", StringComparison.OrdinalIgnoreCase) => "environment",
                    "managed_dir" or "windows_managed_dir" when sectionPath.StartsWith("hooks", StringComparison.OrdinalIgnoreCase) || keyPath.StartsWith("hooks.", StringComparison.OrdinalIgnoreCase) => "environment",
                    "source" or "source_path" or "path" or "directory" or "config_file" or "manifest" when sectionName is "plugins" or "marketplaces" => "plugin",
                    _ => ""
                };
                if (kind.Length == 0) continue;
                foreach (Match quoted in Regex.Matches(assignment.Groups["value"].Value, "(?s)(?<q>['\"])(?<path>.*?)\\k<q>"))
                {
                    var rawPath = quoted.Groups["path"].Value;
                    var localMarketplace = sectionPath.StartsWith("marketplaces.", StringComparison.OrdinalIgnoreCase) && localMarketplaceSections.Contains(sectionPath);
                    if (kind == "plugin" && LooksLikeRemoteSource(rawPath))
                    {
                        findings.Add(new(FindingLevel.Info, "remote-marketplace-rebuild", "此 marketplace 使用远程来源；仅保存配置中的来源名称，恢复后需要联网并重新添加，远程内容和凭据不会写入备份。", full));
                        continue;
                    }
                    if (kind == "plugin" && !LooksLikeLocalPathLiteral(rawPath) && !localMarketplace) {
                        if (key == "source" && sectionPath.StartsWith("marketplaces.", StringComparison.OrdinalIgnoreCase))
                            findings.Add(new(FindingLevel.Info, "remote-marketplace-rebuild", "此 marketplace 使用远程来源；仅保存配置中的来源名称，恢复后需要联网并重新添加，远程内容和凭据不会写入备份。", full));
                        continue;
                    }
                    try
                    {
                        var configured = ResolveConfiguredPath(UnescapeConfig(rawPath), Path.GetDirectoryName(full)!);
                        if (kind == "core") ScanCoreRoot(configured, $"配置的 Codex 数据目录：{full}", token);
                        else if (kind == "sqlite") ScanSqliteLocation(configured, $"配置的 SQLite 状态目录：{full}", token, true);
                        else if (kind == "log")
                        {
                            var log = Add("Codex 日志目录", configured, SourceKind.Environment, true, $"配置的日志目录：{full}");
                            if (!log.Exists) findings.Add(new(FindingLevel.Warning, "configured-log-dir-missing", "Codex 配置指定的日志目录不存在；日志无法随本次备份确认。", configured));
                        }
                        else if (kind == "environment")
                        {
                            var external = Add("Codex 配置引用的外部文件", configured, SourceKind.Environment, true, $"配置路径引用：{full}");
                            if (!external.Exists) findings.Add(new(FindingLevel.Warning, "configured-external-path-missing", "Codex 配置引用的外部文件或目录不存在；恢复后需要重新定位。", configured));
                        }
                        else if (kind == "plugin" && (LooksLikeLocalPathLiteral(rawPath) || localMarketplace))
                        {
                            var external = Add("Codex 插件或市场本地目录", configured, SourceKind.Plugin, true, $"插件/市场路径配置：{full}");
                            if (!external.Exists) findings.Add(new(FindingLevel.Warning, "configured-plugin-path-missing", "配置引用的插件或市场目录不存在或不可访问；恢复后需要重新安装或定位。", configured));
                        }
                        else if (kind == "skill") RegisterSkill(rawPath, $"Codex 技能配置：{full}");
                        else if (kind == "session")
                        {
                            var source = Add("配置的会话目录", configured, SourceKind.Session, true, $"配置文件：{full}");
                            if (!source.Exists) findings.Add(new(FindingLevel.Blocker, "configured-session-missing", "配置的会话目录不存在或不可访问。", configured));
                            else ScanSessionTree(configured, corePath, token);
                        }
                        else AddReferencedProject(configured, $"配置文件显式项目引用：{full}", null, token, corePath);
                    }
                    catch (Exception ex) { findings.Add(new(FindingLevel.Warning, "config-path-invalid", $"配置中的路径无法解析（{ex.GetType().Name}）。", full)); }
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { findings.Add(new(FindingLevel.Warning, "config-coverage-unknown", $"无法读取配置文件中的路径引用（{ex.GetType().Name}）。", full)); }
    }

    private sealed record TomlReadResult(List<string> Statements, bool Truncated);

    private static TomlReadResult ReadTomlStatements(string file, int maxLines)
    {
        var statements = new List<string>();
        var current = new StringBuilder();
        var depth = 0;
        var lineCount = 0;
        var truncated = false;
        foreach (var raw in File.ReadLines(file))
        {
            if (++lineCount > maxLines) { truncated = true; break; }
            var line = StripTomlComment(raw).Trim();
            if (line.Length == 0 && current.Length == 0) continue;
            if (current.Length > 0) current.Append('\n');
            current.Append(line);
            depth += TomlBracketDelta(line);
            if (depth <= 0)
            {
                statements.Add(current.ToString());
                current.Clear();
                depth = 0;
            }
        }
        if (current.Length > 0) statements.Add(current.ToString());
        return new(statements, truncated);
    }

    private static string StripTomlComment(string line)
    {
        var quote = '\0';
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quote == '"' && c == '\\') { i++; continue; }
            if ((quote == '\0' || quote == '"') && c == '"') { quote = quote == '\0' ? '"' : '\0'; continue; }
            if ((quote == '\0' || quote == '\'') && c == '\'') { quote = quote == '\0' ? '\'' : '\0'; continue; }
            if (quote == '\0' && c == '#') return line[..i];
        }
        return line;
    }

    private static int TomlBracketDelta(string line)
    {
        var quote = '\0'; var delta = 0;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quote == '"' && c == '\\') { i++; continue; }
            if ((quote == '\0' || quote == '"') && c == '"') { quote = quote == '\0' ? '"' : '\0'; continue; }
            if ((quote == '\0' || quote == '\'') && c == '\'') { quote = quote == '\0' ? '\'' : '\0'; continue; }
            if (quote != '\0') continue;
            if (c is '[' or '{') delta++; else if (c is ']' or '}') delta--;
        }
        return delta;
    }

    private static bool LooksLikeLocalPathLiteral(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0 || trimmed.Contains("://", StringComparison.Ordinal)) return false;
        return Path.IsPathFullyQualified(trimmed) || trimmed.StartsWith("./", StringComparison.Ordinal) || trimmed.StartsWith(".\\", StringComparison.Ordinal) ||
            trimmed.StartsWith("~/", StringComparison.Ordinal) || trimmed.StartsWith("~\\", StringComparison.Ordinal) || trimmed.Contains('/') || trimmed.Contains('\\') ||
            trimmed.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || trimmed.EndsWith(".toml", StringComparison.OrdinalIgnoreCase) || trimmed.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeRemoteSource(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Contains("://", StringComparison.Ordinal)) return true;
        if (trimmed.StartsWith("git@", StringComparison.OrdinalIgnoreCase)) return true;
        return Regex.IsMatch(trimmed, @"^[^\\/:\s]+@[^\\/:\s]+:");
    }

    private static string ExtractSectionPath(string statement)
    {
        var match = Regex.Match(statement, "(?is)^\\s*\\[\\[?\\s*(?<path>[^\\]]+?)\\s*\\]\\]?\\s*$");
        if (!match.Success) return "";
        return Regex.Replace(match.Groups["path"].Value, "\\s+", "").ToLowerInvariant();
    }

    private void ScanSessionTree(string root, string corePath, CancellationToken token, SessionLifecycle lifecycle = SessionLifecycle.Unknown)
    {
        if (!Directory.Exists(root)) return;
        var queue = new Queue<(string Path, int Depth)>(); queue.Enqueue((root, 0)); var files = 0;
        while (queue.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var (current, depth) = queue.Dequeue();
            try
            {
                foreach (var file in Directory.EnumerateFiles(current, "*.jsonl").Take(10001))
                {
                    if (++files > 10000) { findings.Add(new(FindingLevel.Warning, "session-file-limit", "会话文件超过一万项，后续文件未检测。", root)); return; }
                    ScanSessionJsonl(file, corePath, token, lifecycle);
                }
                if (depth >= 8) { if (Directory.EnumerateDirectories(current).Any()) findings.Add(new(FindingLevel.Warning, "session-depth-limit", "会话目录超过八层，深层内容未检测。", current)); continue; }
                var children = Directory.EnumerateDirectories(current).Take(1001).ToList();
                if (children.Count > 1000) findings.Add(new(FindingLevel.Warning, "session-child-limit", "单层会话目录超过一千项，超出部分未检测。", current));
                foreach (var child in children.Take(1000)) queue.Enqueue((child, depth + 1));
                if (queue.Count > 5000) { findings.Add(new(FindingLevel.Warning, "session-directory-limit", "会话目录数量超过有界检测上限，部分目录未检测。", root)); return; }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { findings.Add(new(FindingLevel.Warning, "session-scan-incomplete", $"无法完整读取会话目录（{ex.GetType().Name}）。", current)); }
        }
    }

    private static SessionLifecycle InferSessionLifecycle(string path)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        if (name.Equals("sessions", StringComparison.OrdinalIgnoreCase)) return SessionLifecycle.Active;
        if (name.Equals("archived", StringComparison.OrdinalIgnoreCase) || name.Equals("archived_sessions", StringComparison.OrdinalIgnoreCase)) return SessionLifecycle.Archived;
        return SessionLifecycle.Unknown;
    }

    private void ScanSessionJsonl(string file, string corePath, CancellationToken token, SessionLifecycle lifecycle = SessionLifecycle.Unknown)
    {
        try
        {
            var (lines, truncated) = ReadBoundedLines(file, 32, 1024 * 1024);
            var lineIndex = 0;
            foreach (var line in lines)
            {
                token.ThrowIfCancellationRequested();
                lineIndex++;
                using var document = JsonDocument.Parse(lineIndex == 1 ? line.TrimStart('\uFEFF') : line, new JsonDocumentOptions { MaxDepth = 32 });
                var root = document.RootElement;
                if (!root.TryGetProperty("type", out var type) || type.GetString() != "session_meta") continue;
                if (lineIndex != 1) findings.Add(new(FindingLevel.Blocker, "session-metadata-unsupported", "会话元数据不在首行，当前恢复器无法安全迁移此格式；已保留会话引用供恢复副本使用。", file));
                var meta = root.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object ? payload : root;
                if (payload.ValueKind != JsonValueKind.Object) findings.Add(new(FindingLevel.Blocker,"session-metadata-unsupported","会话元数据缺少受支持的 payload 字段，无法安全重连源码路径；可保存副本，但不能标记完整迁移。",file));
                static string? Text(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
                DateTimeOffset? activity = null;
                foreach (var name in new[] { "updated_at", "updatedAt", "timestamp", "created_at" })
                    if (meta.TryGetProperty(name, out var value) && TryParseActivity(value, out var parsed)) { activity = parsed; break; }
                AddSession(Text(meta, "id") ?? Text(meta, "session_id"), Text(meta, "title"), corePath, Text(meta, "cwd"), file, activity, $"会话文件元数据：{file}", token, lifecycle);
                return;
            }
            if (truncated) findings.Add(new(FindingLevel.Warning, "session-metadata-truncated", "会话文件开头超过元数据读取上限，未找到可确认的 session_meta。", file));
            findings.Add(new(FindingLevel.Warning, "session-meta-missing", "在限定的文件开头未找到 session_meta，会话关系无法确认。", file));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { findings.Add(new(FindingLevel.Warning, "session-meta-unreadable", $"无法读取会话元数据（{ex.GetType().Name}）。", file)); }
    }

    private void AddSession(string? id, string? title, string corePath, string? projectPath, string? transcriptPath, DateTimeOffset? activity, string evidence, CancellationToken token = default, SessionLifecycle lifecycle = SessionLifecycle.Unknown)
    {
        if (string.IsNullOrWhiteSpace(transcriptPath)) { findings.Add(new(FindingLevel.Blocker,"session-transcript-unknown","会话记录没有提供对话文件位置，无法确认备份齐全。",corePath)); return; }
        string transcript;
        try { transcript = ResolveConfiguredPath(transcriptPath, corePath); }
        catch (Exception ex) { findings.Add(new(FindingLevel.Blocker, "session-path-invalid", $"会话转录路径无效（{ex.GetType().Name}）。", transcriptPath)); return; }
        var sessionItem = Add(string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(transcript) : title, transcript, SourceKind.Session, false, evidence);
        sessionItem.IsDirectory = false;
        if (!sessionItem.Exists) findings.Add(new(FindingLevel.Blocker, "session-transcript-missing", "引用的会话转录文件不存在或不可访问，无法形成完整迁移。", transcript));
        string project = "";
        if (!string.IsNullOrWhiteSpace(projectPath))
        {
            try { project = ResolveConfiguredPath(projectPath, corePath); AddReferencedProject(project, evidence, activity, token, corePath); }
            catch (Exception ex) { findings.Add(new(FindingLevel.Warning, "session-project-invalid", $"会话引用的项目路径无效（{ex.GetType().Name}）。", projectPath)); }
        }
        var normalizedCore = Normalize(corePath);
        var sessionId = string.IsNullOrWhiteSpace(id) ? Path.GetFileNameWithoutExtension(transcript) : id;
        var existing = sessions.FirstOrDefault(x => Paths.Equals(x.CorePath, normalizedCore) && Paths.Equals(x.TranscriptPath, transcript) && Paths.Equals(x.ProjectPath, project) && x.Id == sessionId);
        if (existing is null) sessions.Add(new() { Id = sessionId, Title = title ?? "", CorePath = normalizedCore, ProjectPath = project, TranscriptPath = transcript, LastActivityUtc = activity, Lifecycle = lifecycle });
        else
        {
            if (activity > existing.LastActivityUtc) existing.LastActivityUtc = activity;
            if (existing.Lifecycle == SessionLifecycle.Unknown || lifecycle == SessionLifecycle.Active) existing.Lifecycle = lifecycle;
            if (string.IsNullOrWhiteSpace(existing.Title) && !string.IsNullOrWhiteSpace(title)) existing.Title = title;
        }
    }

    private static (List<string> Lines, bool Truncated) ReadBoundedLines(string file, int maxLines, int maxBytes)
    {
        var lines = new List<string>(); var bytes = new List<byte>(); var total = 0; var truncated = false;
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        while (lines.Count < maxLines && total < maxBytes)
        {
            var value = stream.ReadByte(); if (value < 0) { if (bytes.Count > 0) lines.Add(System.Text.Encoding.UTF8.GetString([.. bytes])); return (lines, truncated); }
            total++; if (value == '\n') { lines.Add(System.Text.Encoding.UTF8.GetString([.. bytes]).TrimEnd('\r')); bytes.Clear(); }
            else if (bytes.Count < 256 * 1024) bytes.Add((byte)value); else truncated = true;
        }
        if (stream.Position < stream.Length && lines.Count < maxLines) truncated = true;
        return (lines, truncated);
    }

    private static bool LooksLikeCoreRoot(string path) => File.Exists(Path.Combine(path, "config.toml")) || Directory.Exists(Path.Combine(path, "sessions")) ||
        EnumerateTopFiles(path, ["state*.sqlite", "logs*.sqlite", "goals*.sqlite", "memories*.sqlite", "queue*.sqlite", "thread_history*.sqlite"]).Any();

    private string ResolveConfiguredPath(string value, string baseDirectory)
    {
        var path = UnescapeConfig(value.Trim());
        // Preserve Windows extended and UNC prefixes before checking whether the value is rooted.
        if (path.StartsWith(@"\?\", StringComparison.Ordinal)) path = @"\" + path;
        if (path == "~") path = selectedProfile;
        else if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal)) path = Path.Combine(selectedProfile, path[2..]);
        var hostProfile = Normalize(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (Paths.Equals(selectedProfile, hostProfile))
            path = Environment.ExpandEnvironmentVariables(path);
        else
        {
            path = path.Replace("%USERPROFILE%", selectedProfile, StringComparison.OrdinalIgnoreCase)
                .Replace("%HOME%", selectedProfile, StringComparison.OrdinalIgnoreCase)
                .Replace("%APPDATA%", Path.Combine(selectedProfile, "AppData", "Roaming"), StringComparison.OrdinalIgnoreCase)
                .Replace("%LOCALAPPDATA%", Path.Combine(selectedProfile, "AppData", "Local"), StringComparison.OrdinalIgnoreCase)
                .Replace("%CODEX_HOME%", Path.Combine(selectedProfile, ".codex"), StringComparison.OrdinalIgnoreCase);
            if (path.Contains('%', StringComparison.Ordinal))
                throw new BackupException("自定义用户配置包含无法验证的环境变量；为避免读到宿主用户文件，已要求先改成明确的本地路径。");
        }
        if (!Path.IsPathFullyQualified(path))
        {
            if (Path.IsPathRooted(path) || path.StartsWith("/", StringComparison.Ordinal) || path.StartsWith("\\", StringComparison.Ordinal))
                throw new BackupException("配置引用了网络、设备或 POSIX 根路径；请先把内容导出到本地磁盘，再重新扫描。");
            path = Path.Combine(baseDirectory, path);
        }
        return Normalize(path);
    }

    private static string UnescapeConfig(string value)
    {
        // A TOML literal UNC/device path must keep its leading slashes. Basic strings still
        // receive the bounded compatibility unescape used by older config files.
        if (value.StartsWith(@"\\", StringComparison.Ordinal) && !value.StartsWith(@"\\\\?\", StringComparison.Ordinal)) return value;
        var unescaped = value.Replace("\\\\", "\\").Replace("\\\"", "\"").Replace("\\'", "'");
        if (unescaped.StartsWith(@"\?\", StringComparison.Ordinal)) return @"\" + unescaped;
        return unescaped;
    }

    private void AddReferencedProject(string? path, string evidence, DateTimeOffset? activity = null, CancellationToken token = default, string? associatedCorePath = null)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return;
        SourceItem item;
        try { item = Add(Path.GetFileName(Path.TrimEndingDirectorySeparator(path)), path, SourceKind.Project, false, evidence); }
        catch (Exception ex) { findings.Add(new(FindingLevel.Warning, "project-path-invalid", $"项目路径格式无效，无法纳入检测（{ex.GetType().Name}）。", path)); return; }
        ApplyActivity(item, activity, evidence);
        if (!item.Exists) findings.Add(new(FindingLevel.Warning, "referenced-project-missing", "历史引用的项目路径不存在或不可访问；未覆盖，请定位原文件或保留此遗漏说明。", item.Path));
        if (item.Exists && item.IsDirectory)
        {
            AddGitDependencies(item);
            ScanProjectConfig(item.Path, associatedCorePath ?? items.FirstOrDefault(x => x.Kind == SourceKind.Core && x.Exists)?.Path ?? Path.Combine(selectedProfile, ".codex"), token);
        }
    }

    private void AddGitDependencies(SourceItem project)
    {
        var dotGit = Path.Combine(project.Path, ".git");
        if (Directory.Exists(dotGit)) { var dependency = Add("Git 元数据", dotGit, SourceKind.Environment, true, "项目 .git 目录"); Link(project, dependency); return; }
        if (!File.Exists(dotGit)) return;
        try
        {
            if (new FileInfo(dotGit).Length > 64 * 1024) throw new IOException(".git 指针超出读取上限");
            var text = File.ReadAllText(dotGit).Trim();
            if (!text.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase)) { findings.Add(new(FindingLevel.Warning, "git-pointer-unknown", ".git 文件格式无法识别，工作树依赖未完整覆盖。", dotGit)); return; }
            var raw = text[7..].Trim();
            var gitDir = Path.GetFullPath(Path.IsPathFullyQualified(raw) ? raw : Path.Combine(project.Path, raw));
            var git = Add("Git 工作树元数据", gitDir, SourceKind.Environment, true, $"gitdir 指针：{dotGit}"); Link(project, git);
            if (!git.Exists) findings.Add(new(FindingLevel.Blocker, "gitdir-missing", "工作树指针（gitdir）指向的元数据不存在或不可访问。", git.Path));
            var commonPointer = Path.Combine(gitDir, "commondir");
            if (File.Exists(commonPointer))
            {
                if (new FileInfo(commonPointer).Length > 64 * 1024) throw new IOException("commondir 指针超出读取上限");
                var rawCommon = File.ReadAllText(commonPointer).Trim();
                var commonPath = Path.GetFullPath(Path.IsPathFullyQualified(rawCommon) ? rawCommon : Path.Combine(gitDir, rawCommon));
                var common = Add("Git 公共元数据", commonPath, SourceKind.Environment, true, $"commondir 指针：{commonPointer}"); Link(project, common);
                if (!common.Exists) findings.Add(new(FindingLevel.Blocker, "git-commondir-missing", "公共目录指针（commondir）指向的 Git 元数据不存在或不可访问。", common.Path));
            }
        }
        catch (Exception ex) { findings.Add(new(FindingLevel.Warning, "git-dependency-unknown", $"无法解析 Git 工作树依赖（{ex.GetType().Name}）。", dotGit)); }
    }

    private SourceItem Add(string name, string path, SourceKind kind, bool required, string evidence)
    {
        var full = Normalize(path);
        var existing = items.FirstOrDefault(x => Paths.Equals(x.Path, full) && x.Kind == kind);
        if (existing is not null) { existing.Required |= required; if (!existing.DiscoveredBy.Contains(evidence, StringComparison.OrdinalIgnoreCase)) existing.DiscoveredBy += $"; {evidence}"; return existing; }
        var directory = Directory.Exists(full); var file = File.Exists(full);
        var item = new SourceItem { Name = string.IsNullOrWhiteSpace(name) ? full : name, Path = full, Kind = kind, Required = required, Selected = required || directory || file, Exists = directory || file, IsDirectory = directory || !file, Reason = required ? "必需配置、独有资源或已选内容依赖，必须保留。" : "默认选中已存在的项目；取消后会列入未覆盖范围。", DiscoveredBy = evidence, Notes = WindowsEnvironment.DescribeVolume(full) };
        try { if (item.Exists) item.LastModifiedUtc = item.IsDirectory ? Directory.GetLastWriteTimeUtc(full) : File.GetLastWriteTimeUtc(full); } catch { }
        items.Add(item); return item;
    }

    private static void ApplyActivity(SourceItem item, DateTimeOffset? activity, string evidence)
    {
        if (activity is null || item.LastActivityUtc >= activity) return;
        item.LastActivityUtc = activity;
        var note = $"活动时间来自元数据：{evidence}；不以目录修改时间替代。";
        if (!item.Notes.Contains(note, StringComparison.Ordinal)) item.Notes += $"；{note}";
    }

    private static bool IsActivityColumn(string name)
    {
        var normalized = Regex.Replace(name, "([a-z])([A-Z])", "$1_$2").Replace('-', '_').ToLowerInvariant();
        return normalized is "updated_at" or "updated" or "last_activity" or "last_activity_at" or "modified_at";
    }

    private static bool TryParseActivity(JsonElement value, out DateTimeOffset activity) =>
        value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => TryParseActivity(number, out activity),
            JsonValueKind.String => TryParseActivity(value.GetString(), out activity),
            _ => FailActivity(out activity)
        };

    private static bool TryParseActivity(object? value, out DateTimeOffset activity)
    {
        if (value is long number) return TryParseActivity(number, out activity);
        if (value is int integer) return TryParseActivity((long)integer, out activity);
        return TryParseActivity(value?.ToString(), out activity);
    }

    private static bool TryParseActivity(string? value, out DateTimeOffset activity)
    {
        if (long.TryParse(value, out var number)) return TryParseActivity(number, out activity);
        return DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out activity);
    }

    private static bool TryParseActivity(long value, out DateTimeOffset activity)
    {
        try { activity = Math.Abs(value) >= 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(value) : DateTimeOffset.FromUnixTimeSeconds(value); return true; }
        catch { return FailActivity(out activity); }
    }

    private static bool FailActivity(out DateTimeOffset activity) { activity = default; return false; }

    private static void Link(SourceItem owner, SourceItem dependency) { if (!owner.DependencyIds.Contains(dependency.Id)) owner.DependencyIds.Add(dependency.Id); }
    private static string DiscoverVersion(IEnumerable<string> roots)
    {
        foreach (var root in roots)
        {
            var versionFile = Path.Combine(root, "version.txt");
            try { if (File.Exists(versionFile) && new FileInfo(versionFile).Length <= 64 * 1024) { var version = File.ReadAllText(versionFile).Trim(); if (!string.IsNullOrWhiteSpace(version)) return version; } } catch { }
            var packageFile = Path.Combine(root, "package.json");
            try
            {
                if (!File.Exists(packageFile) || new FileInfo(packageFile).Length > 1024 * 1024) continue;
                using var document = JsonDocument.Parse(File.ReadAllText(packageFile));
                if (document.RootElement.TryGetProperty("version", out var value) && !string.IsNullOrWhiteSpace(value.GetString())) return value.GetString()!;
            }
            catch { }
        }
        return "未知";
    }
    private static string Normalize(string path)
    {
        var value = Environment.ExpandEnvironmentVariables(path.Trim());
        if (value.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            if (value.Length < 7 || !char.IsAsciiLetter(value[4]) || value[5] != ':' || value[6] is not ('\\' or '/'))
                throw new BackupException("不支持网络或设备扩展路径。");
            value = value[4..];
        }
        if (value.StartsWith(@"\\", StringComparison.Ordinal)) throw new BackupException("不支持网络或设备路径。");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
    }
    private static bool IsPathColumn(string? name)
    {
        if (name is null) return false;
        var normalized = Regex.Replace(name, "([a-z])([A-Z])", "$1_$2").Replace('-', '_').ToLowerInvariant();
        if (normalized.Contains("rollout") || normalized.Contains("log") || normalized.Contains("archive")) return false;
        return normalized is "path" or "cwd" or "directory" or "root" or "roots" or "root_path" or "root_paths" or "project_path" or "project_sources" or "workspace_path" or "workspace_root" or "workspace_roots" or "runtime_workspace_roots" or "electron_saved_workspace_roots";
    }
    private static bool IsPathValueMap(string? name) => name is "thread-workspace-root" or "thread_workspace_root" or "thread-projectless-output-directories";
    private static bool IsPathKeyMap(string? name) => name is "project_labels" or "project-labels" or "electron-workspace-root-labels";
    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
    private static IEnumerable<string> EnumerateTopFiles(string root, IEnumerable<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly).ToList(); } catch { continue; }
            foreach (var file in files) yield return file;
        }
    }
}
