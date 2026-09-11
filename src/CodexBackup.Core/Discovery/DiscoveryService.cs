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

    public Task<ScanResult> ScanAsync(string? profile = null, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        lock (scanGate)
        {
        cancellationToken.ThrowIfCancellationRequested();
        var current = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var selectedProfile = PathSafety.Full(string.IsNullOrWhiteSpace(profile) ? current : profile);
        var includeProcessEnvironment = Paths.Equals(selectedProfile, current);
        var result = new ScanResult { UserProfile = selectedProfile };
        items.Clear(); findings.Clear(); packageInstallations.Clear(); packageVersions.Clear();

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

        foreach (var root in roots.DistinctBy(x => Normalize(x.Path), Paths))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var required = Directory.Exists(root.Path) || roots.Count == 1 || root.Evidence.Contains("CODEX_HOME", StringComparison.Ordinal);
            var core = Add("Codex 会话、配置与核心数据", root.Path, SourceKind.Core, required, root.Evidence);
            if (!core.Exists && required) findings.Add(new(FindingLevel.Blocker, "required-codex-root-missing", "必需的 Codex 数据目录不存在或不可访问，请核对用户目录或 CODEX_HOME。", core.Path));
            ScanCodexRoot(core.Path, cancellationToken);
        }

        ScanProfileLocations(selectedProfile, cancellationToken);
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
        result.CodexVersion = packageVersions.FirstOrDefault() ?? DiscoverVersion(roots.Select(x => Normalize(x.Path)));
        if (result.CodexVersion == "未知") findings.Add(new(FindingLevel.Info, "codex-version-unknown", "未能从已知版本文件确认 Codex 版本；不会执行未知程序来探测。"));
        foreach (var missing in items.Where(x => !x.Exists && !x.Required))
            findings.Add(new(FindingLevel.Info, "known-location-missing", "此用户没有该可选位置，默认不选择。", missing.Path));
        var writers = includeProcessEnvironment ? WindowsEnvironment.GetWriterNames() : [];
        if (writers.Count > 0) findings.Add(new(FindingLevel.Warning, "active-writers", $"相关程序正在运行，最终备份前必须退出：{string.Join(", ", writers)}"));
        findings.Add(new(FindingLevel.Warning, "coverage-boundary", "当前仅覆盖已知位置和解析出的引用；外部数据库、WSL/Docker、网络位置、其他用户和未知版本字段需另行核查。"));

        result.Items = [.. items];
        result.Findings = [.. findings];
        progress?.Report(new("检测", $"发现 {items.Count} 项来源"));
        return Task.FromResult(result);
        }
    }

    private void ScanCodexRoot(string root, CancellationToken token)
    {
        if (!Directory.Exists(root)) return;
        AddChildren(Path.Combine(root, "skills"), SourceKind.Skill, "Codex 技能目录");
        AddChildren(Path.Combine(root, "plugins"), SourceKind.Plugin, "Codex 插件目录");
        AddChildren(Path.Combine(root, "marketplaces"), SourceKind.Plugin, "Codex 插件市场目录");
        ScanManagedWorktrees(Path.Combine(root, "worktrees"), token);

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
            if (name.Contains("config", StringComparison.OrdinalIgnoreCase) || name.Contains("project", StringComparison.OrdinalIgnoreCase) || name.Contains("global-state", StringComparison.OrdinalIgnoreCase)) ScanJson(file);
        }
        foreach (var file in EnumerateTopFiles(root, ["state*.sqlite", "*.db"])) ScanSqlite(file, token);
        if (Directory.Exists(Path.Combine(root, "sqlite")))
            foreach (var file in EnumerateTopFiles(Path.Combine(root, "sqlite"), ["*.db", "*.sqlite"])) ScanSqlite(file, token);
        ScanConfigReferences(root);
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
        Add("普通任务工作文件", Path.Combine(profile, "Documents", "Codex"), SourceKind.Project, false, "Codex 普通任务默认位置");
        if (Paths.Equals(profile, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)))
            Add("已重定向的普通任务工作文件", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Codex"), SourceKind.Project, false, "Windows 已知文件夹配置");
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

    private void ScanJson(string file)
    {
        try
        {
            if (new FileInfo(file).Length > 16 * 1024 * 1024) throw new IOException("元数据超出读取上限");
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            WalkJson(document.RootElement, null, file, null);
        }
        catch (Exception ex) { findings.Add(new(FindingLevel.Warning, "metadata-json-unreadable", $"无法解析 JSON 元数据（{ex.GetType().Name}）。", file)); }
    }

    private void WalkJson(JsonElement element, string? property, string evidence, DateTimeOffset? inheritedActivity)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var activity = inheritedActivity;
            foreach (var member in element.EnumerateObject())
                if (IsActivityColumn(member.Name) && TryParseActivity(member.Value, out var parsed)) activity = parsed;
            foreach (var member in element.EnumerateObject())
            {
                if (Path.IsPathFullyQualified(member.Name)) AddReferencedProject(member.Name, evidence, activity);
                WalkJson(member.Value, IsPathColumn(property) ? property : member.Name, evidence, activity);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) WalkJson(child, property, evidence, inheritedActivity);
        else if (element.ValueKind == JsonValueKind.String && IsPathColumn(property)) AddReferencedProject(element.GetString(), evidence, inheritedActivity);
    }

    private void ScanSqlite(string file, CancellationToken token)
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
                        AddReferencedProject(reader.GetValue(0)?.ToString(), $"SQLite 元数据 {Path.GetFileName(file)}:{table}.{column}", activity);
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

    private void ScanConfigReferences(string root)
    {
        foreach (var name in new[] { "config.toml", "config.yaml", "config.yml" })
        {
            var file = Path.Combine(root, name); if (!File.Exists(file)) continue;
            try
            {
                if (new FileInfo(file).Length > 4 * 1024 * 1024) throw new IOException("配置文件超出读取上限");
                foreach (var line in File.ReadLines(file))
                {
                    if (!Regex.IsMatch(line, "(?i)^\\s*(project_path|workspace(?:_root|_path)?|cwd|root(?:_path)?|directory)\\s*[=:]")) continue;
                    var match = Regex.Match(line, "[=:]\\s*[\"']?(?<path>(?:[A-Za-z]:[\\\\/]|/)[^\"'#]+)");
                    if (match.Success) AddReferencedProject(match.Groups["path"].Value.Trim(), $"配置文件显式引用：{file}");
                }
            }
            catch (Exception ex) { findings.Add(new(FindingLevel.Warning, "config-coverage-unknown", $"无法读取配置文件中的路径引用（{ex.GetType().Name}）。", file)); }
        }
    }

    private void AddReferencedProject(string? path, string evidence, DateTimeOffset? activity = null)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return;
        SourceItem item;
        try { item = Add(Path.GetFileName(Path.TrimEndingDirectorySeparator(path)), path, SourceKind.Project, false, evidence); }
        catch (Exception ex) { findings.Add(new(FindingLevel.Warning, "project-path-invalid", $"项目路径格式无效，无法纳入检测（{ex.GetType().Name}）。", path)); return; }
        ApplyActivity(item, activity, evidence);
        if (!item.Exists) findings.Add(new(FindingLevel.Warning, "referenced-project-missing", "历史引用的项目路径不存在或不可访问；未覆盖，请定位原文件或保留此遗漏说明。", item.Path));
        if (item.Exists && item.IsDirectory) AddGitDependencies(item);
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
        return normalized is "path" or "cwd" or "directory" or "root" or "roots" or "root_path" or "root_paths" or "project_path" or "workspace_path" || normalized.Contains("workspace_root");
    }
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
