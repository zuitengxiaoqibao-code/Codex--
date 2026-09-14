namespace CodexBackup.Core;

public static class BackupScopePolicy
{
    public static bool SelectByDefault(SourceItem source, bool discoveredAsRequired) =>
        MustPreserve(source, discoveredAsRequired) || source.Exists && source.Kind != SourceKind.Application && !IsRebuildableLogOrCache(source);

    public static bool MustPreserve(SourceItem source, bool discoveredAsRequired)
    {
        if (!discoveredAsRequired) return false;
        if (source.Kind is SourceKind.Application or SourceKind.Tool) return false;
        if (IsRebuildableLogOrCache(source)) return false;
        return source.Kind is SourceKind.Core or SourceKind.Project or SourceKind.Session or SourceKind.Memory or SourceKind.Skill or SourceKind.Plugin
            || IsPersonalConfiguration(source)
            || IsProjectDependency(source);
    }

    public static string Explanation(SourceItem source) => source.Kind switch
    {
        SourceKind.Application => "Codex / ChatGPT 程序和应用缓存可在新系统重新安装，默认不备份",
        SourceKind.Tool => "外围工具配置建议按需保存，恢复后仍需重新安装对应工具",
        SourceKind.Environment when IsRebuildableLogOrCache(source) => "日志或缓存可重新生成，默认不作为必须项",
        SourceKind.Environment => "个人配置或项目依赖，建议保存；系统策略需在新系统重新配置",
        SourceKind.Custom => "用户手动加入的内容",
        _ => "重装后可能无法自动找回，建议保存"
    };

    private static bool IsPersonalConfiguration(SourceItem source)
    {
        var text = string.Join(" ", source.Name, source.Path, source.DiscoveredBy);
        return text.Contains("config.toml", StringComparison.OrdinalIgnoreCase)
            || text.Contains("requirements.toml", StringComparison.OrdinalIgnoreCase)
            || text.Contains("instructions", StringComparison.OrdinalIgnoreCase)
            || text.Contains("AGENTS.md", StringComparison.OrdinalIgnoreCase)
            || text.Contains("配置档", StringComparison.OrdinalIgnoreCase)
            || text.Contains("用户配置", StringComparison.OrdinalIgnoreCase)
            || text.Contains("SQLite 状态", StringComparison.OrdinalIgnoreCase)
            || text.Contains("state_", StringComparison.OrdinalIgnoreCase)
            || text.Contains("thread_history", StringComparison.OrdinalIgnoreCase)
            || text.Contains("memories", StringComparison.OrdinalIgnoreCase)
            || text.Contains("goals", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsProjectDependency(SourceItem source)
    {
        var text = string.Join(" ", source.Name, source.Path, source.DiscoveredBy);
        return text.Contains("Git 元数据", StringComparison.OrdinalIgnoreCase)
            || text.Contains("gitdir", StringComparison.OrdinalIgnoreCase)
            || text.Contains("commondir", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsRebuildableLogOrCache(SourceItem source)
    {
        var text = string.Join(" ", source.Name, source.Path, source.DiscoveredBy);
        return text.Contains("日志", StringComparison.OrdinalIgnoreCase)
            || text.Contains("logs_", StringComparison.OrdinalIgnoreCase)
            || text.Contains("cache", StringComparison.OrdinalIgnoreCase)
            || text.Contains("temp", StringComparison.OrdinalIgnoreCase);
    }
}
