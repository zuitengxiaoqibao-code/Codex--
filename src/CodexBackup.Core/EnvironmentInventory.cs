namespace CodexBackup.Core;

using Microsoft.Win32;

public sealed class EnvironmentManifest
{
    public string CollectedUtc { get; set; } = DateTimeOffset.UtcNow.ToString("O");
    public string RedactionPolicy { get; set; } = "只保存名称、版本和路径摘要；密钥、令牌、Cookie、密码和私钥值不会保存。";
    public List<EnvironmentEntry> Entries { get; set; } = [];
}

public sealed class EnvironmentEntry
{
    public string Category { get; set; } = "其他";
    public string DisplayName { get; set; } = "";
    public string ValueSummary { get; set; } = "未知";
    public string Risk { get; set; } = "信息";
    public string Coverage { get; set; } = "仅检测到";
    public string? SourcePath { get; set; }
}

public static class EnvironmentInventory
{
    private static readonly string[] LockfileNames =
    [
        "requirements.txt", "pyproject.toml", "poetry.lock", "Pipfile.lock",
        "package.json", "package-lock.json", "npm-shrinkwrap.json", "pnpm-lock.yaml", "yarn.lock",
        "Cargo.toml", "Cargo.lock", "go.mod", "go.sum", "pom.xml", "build.gradle",
        "global.json", "Directory.Build.props", ".gitmodules", ".gitattributes",
        "config.toml", "requirements.toml"
    ];

    public static EnvironmentManifest Collect(string profile, IReadOnlyList<SourceItem> items, CancellationToken cancellationToken = default, bool includeHostEnvironment = true, IReadOnlyList<Finding>? findings = null)
    {
        var manifest = new EnvironmentManifest();
        Add(manifest, "运行环境", ".NET 运行时", Environment.Version.ToString(), "信息");
        Add(manifest, "运行环境", "操作系统", Environment.OSVersion.VersionString, "信息");
        Add(manifest, "运行环境", "进程架构", Environment.Is64BitProcess ? "x64" : "x86", "信息");

        foreach (var variable in ReadVariables(cancellationToken, includeHostEnvironment))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sensitive = IsSensitiveName(variable.Key);
            Add(manifest, "环境变量", variable.Key, sensitive ? "已隐藏敏感值" : string.IsNullOrEmpty(variable.Value) ? "未设置" : "已设置", risk: sensitive ? "敏感" : "信息", coverage: sensitive ? "仅保存名称" : "仅保存状态");
        }

        var roots = items.Where(x => x.Exists && x.IsDirectory && x.Kind is SourceKind.Project or SourceKind.Core)
            .Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).Take(1000).ToList();
        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var name in LockfileNames)
            {
                var path = Path.Combine(root, name);
                if (File.Exists(path)) Add(manifest, "锁定文件", name, "已发现，恢复后可据此重建依赖", path, "信息", "随对应来源选择");
            }
            var git = Path.Combine(root, ".git");
            if (Directory.Exists(git) || File.Exists(git)) Add(manifest, "Git", ".git", "项目包含 Git 关联，恢复时按路径映射检查", git, "重要", "随对应来源选择");
        }

        // Surface every discovered Codex source in the report so users can verify that
        // history, runtime databases, skills and plugins are covered alongside config files.
        foreach (var item in items.Where(x => x.Exists && x.Kind is SourceKind.Core or SourceKind.Session or SourceKind.Memory or SourceKind.Skill or SourceKind.Plugin or SourceKind.Environment))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var category = item.Kind switch
            {
                SourceKind.Core => "Codex 会话主数据",
                SourceKind.Session => "会话与转录",
                SourceKind.Memory => "记忆库",
                SourceKind.Skill => "技能",
                SourceKind.Plugin => "插件与市场",
                _ => "Codex 外围状态"
            };
            var coverage = item.Required ? "已纳入备份" : "随对应来源选择";
            Add(manifest, category, item.Name, string.IsNullOrWhiteSpace(item.Reason) ? "已发现；请按来源选择并在恢复后复核" : item.Reason, item.Path, item.Required ? "重要" : "信息", coverage);
        }

        foreach (var config in items.Where(x => x.Exists && !x.IsDirectory && x.Kind == SourceKind.Environment &&
                     (Path.GetFileName(x.Path) is "config.toml" or "requirements.toml" or "managed_config.toml" || Path.GetFileName(x.Path).EndsWith(".config.toml", StringComparison.OrdinalIgnoreCase))))
            Add(manifest, "Codex 配置层", Path.GetFileName(config.Path), "已列入备份；新系统需按来源版本和管理员策略重新核对", config.Path, config.Path.EndsWith("requirements.toml", StringComparison.OrdinalIgnoreCase) ? "需核查" : "重要", "已纳入备份");

        var knownPaths = new[]
        {
            ("WSL/Docker", "WSL 配置", Path.Combine(profile, ".wslconfig")),
            ("WSL/Docker", "Docker 配置", Path.Combine(profile, ".docker")),
            ("WSL/Docker", "Docker Desktop 数据", Path.Combine(profile, "AppData", "Local", "Docker")),
            ("运行环境", ".NET 共享运行时", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "shared")),
            ("运行环境", "Node.js 用户安装", Path.Combine(profile, "AppData", "Local", "Programs", "nodejs")),
            ("运行环境", "Python 用户安装", Path.Combine(profile, "AppData", "Local", "Programs", "Python"))
        };
        foreach (var (category, name, path) in knownPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(path) || File.Exists(path)) Add(manifest, category, name, "已发现位置；版本和服务需在新系统重新核对", path, "信息", "仅检测到");
        }

        Add(manifest, "Windows 服务", "系统服务", "未自动读取或恢复；请根据项目清单人工核对", null, "需核查", "需在新系统重建");
        Add(manifest, "计划任务", "Windows 计划任务", "未自动读取或恢复；请根据项目清单人工核对", null, "需核查", "需在新系统重建");
        Add(manifest, "端口", "本机监听端口", "未自动读取或恢复；请根据项目清单人工核对", null, "需核查", "需在新系统重建");
        Add(manifest, "文件关联", "Windows 文件关联", "未自动修改；新系统需按项目需要重新注册", null, "需核查", "需在新系统重建");
        Add(manifest, "Codex 配置层", "云端或组织受管配置", "官方配置可能由云端、MDM 或域策略提供；本工具不会下载、复制或自动启用它", null, "需核查", "外部来源，未复制");
        foreach (var finding in findings?.Where(x => x.Code == "remote-marketplace-rebuild") ?? [])
            Add(manifest, "插件与市场", "远程 marketplace", finding.Message, null, "需核查", "需在新系统重建");
        return manifest;
    }

    internal static EnvironmentManifest CollectFromVariables(IEnumerable<KeyValuePair<string, string?>> variables)
    {
        var manifest = new EnvironmentManifest();
        foreach (var variable in variables.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            var sensitive = IsSensitiveName(variable.Key);
            Add(manifest, "环境变量", variable.Key, sensitive ? "已隐藏敏感值" : string.IsNullOrEmpty(variable.Value) ? "未设置" : "已设置", risk: sensitive ? "敏感" : "信息", coverage: sensitive ? "仅保存名称" : "仅保存状态");
        }
        return manifest;
    }

    private static IEnumerable<KeyValuePair<string, string?>> ReadVariables(CancellationToken cancellationToken, bool includeHostEnvironment)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var scopes = includeHostEnvironment
            ? new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine }
            : new[] { EnvironmentVariableTarget.Machine };
        foreach (var scope in scopes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables(scope))
                {
                    if (entry.Key is string key) values[key] = entry.Value?.ToString();
                }
            }
            catch (System.Security.SecurityException) { }
            catch (PlatformNotSupportedException) { }
        }
        return values.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Take(2000).Select(x => new KeyValuePair<string, string?>(x.Key, x.Value));
    }

    private static bool IsSensitiveName(string name)
    {
        var value = name.ToLowerInvariant();
        return value.Contains("key") || value.Contains("token") || value.Contains("secret") || value.Contains("password") || value.Contains("passwd") || value.Contains("cookie") || value.Contains("private") || value.Contains("auth");
    }

    private static void Add(EnvironmentManifest manifest, string category, string displayName, string summary, string? sourcePath = null, string risk = "信息", string coverage = "仅检测到")
    {
        if (manifest.Entries.Any(x => x.Category == category && x.DisplayName.Equals(displayName, StringComparison.OrdinalIgnoreCase) && string.Equals(x.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase))) return;
        manifest.Entries.Add(new EnvironmentEntry { Category = category, DisplayName = displayName, ValueSummary = summary, Risk = risk, Coverage = coverage, SourcePath = sourcePath });
    }
}
