using System.Text.RegularExpressions;

namespace CodexBackup.Core;

/// <summary>
/// Reports obsolete configuration documented by the current OpenAI Codex configuration reference.
/// It is intentionally read-only and never guesses that an undocumented file is safe to delete.
/// </summary>
public static class OfficialConfigAudit
{
    public const string ReferenceUrl = "https://learn.chatgpt.com/docs/config-file/config-reference";

    private sealed record Rule(string Key, string Code, FindingLevel Level, string Replacement, string Status);

    private static readonly IReadOnlyDictionary<string, Rule> Rules = new Dictionary<string, Rule>(StringComparer.OrdinalIgnoreCase)
    {
        ["agents.max_threads"] = Legacy("agents.max_threads", "agents.max_concurrent_threads_per_session"),
        ["background_terminal_timeout"] = Legacy("background_terminal_timeout", "background_terminal_max_timeout"),
        ["experimental_use_unified_exec_tool"] = Legacy("experimental_use_unified_exec_tool", "features.unified_exec"),
        ["features.codex_hooks"] = Deprecated("features.codex_hooks", "features.hooks"),
        ["features.web_search"] = Deprecated("features.web_search", "顶层 web_search"),
        ["features.web_search_cached"] = Deprecated("features.web_search_cached", "web_search = \"cached\""),
        ["features.web_search_request"] = Deprecated("features.web_search_request", "web_search = \"live\""),
        ["memories.no_memories_if_mcp_or_web_search"] = Legacy("memories.no_memories_if_mcp_or_web_search", "memories.disable_on_external_context"),
        ["shell_environment_policy.exclude"] = Legacy("shell_environment_policy.exclude", "shell_environment_policy.filters"),
        ["shell_environment_policy.include_only"] = Legacy("shell_environment_policy.include_only", "shell_environment_policy.filters")
    };

    public static IReadOnlyList<Finding> Inspect(IReadOnlyList<string> statements, string path)
    {
        var results = new List<Finding>();
        var fileName = Path.GetFileName(path);
        if (fileName.Equals("managed_config.toml", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("config.yaml", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("config.yml", StringComparison.OrdinalIgnoreCase))
        {
            results.Add(new(
                FindingLevel.Info,
                "official-config-format-unconfirmed",
                $"当前官方配置参考只确认 config.toml、requirements.toml 和 <name>.config.toml；没有确认 {fileName} 是否仍由当前版本读取。程序会保留它，不会断定它已经停用，也不会自动删除。",
                path));
        }

        var section = "";
        foreach (var raw in statements)
        {
            var statement = StripComment(raw).Trim();
            var sectionMatch = Regex.Match(statement, "^\\s*\\[\\[?\\s*(?<name>.+?)\\s*\\]\\]?\\s*$", RegexOptions.CultureInvariant);
            if (sectionMatch.Success)
            {
                section = NormalizeSection(sectionMatch.Groups["name"].Value);
                continue;
            }

            var assignment = Regex.Match(statement, "^\\s*(?<key>[A-Za-z0-9_.-]+)\\s*=\\s*(?<value>.*?)\\s*$", RegexOptions.CultureInvariant);
            if (!assignment.Success) continue;
            var key = assignment.Groups["key"].Value;
            var qualifiedKey = string.IsNullOrWhiteSpace(section) ? key : section + "." + key;
            var value = assignment.Groups["value"].Value.Trim().Trim('"', '\'');

            if (qualifiedKey.Equals("approval_policy", StringComparison.OrdinalIgnoreCase))
            {
                if (value.Equals("untrusted", StringComparison.OrdinalIgnoreCase))
                    results.Add(new(FindingLevel.Warning, "official-unsupported-config", "官方已不支持 approval_policy = \"untrusted\"。请删除该项，或按使用方式改为 on-request 或 never；项目的 trust_level = \"untrusted\" 不受此规则影响。", path));
                else if (value.Equals("on-failure", StringComparison.OrdinalIgnoreCase))
                    results.Add(new(FindingLevel.Warning, "official-deprecated-config", "官方已弃用 approval_policy = \"on-failure\"。交互运行请改为 on-request，非交互运行可使用 never。", path));
                continue;
            }

            if (!Rules.TryGetValue(qualifiedKey, out var rule)) continue;
            results.Add(new(rule.Level, rule.Code, $"官方将 {rule.Key} 标为{rule.Status}；新配置请使用 {rule.Replacement}。原配置仍会备份，本工具不会自动改写。", path));
        }

        return results.DistinctBy(finding => (finding.Code, finding.Message)).ToList();
    }

    private static Rule Deprecated(string key, string replacement) => new(key, "official-deprecated-config", FindingLevel.Warning, replacement, "已弃用");
    private static Rule Legacy(string key, string replacement) => new(key, "official-legacy-config", FindingLevel.Info, replacement, "旧名称或旧别名");

    private static string NormalizeSection(string value)
    {
        return string.Join('.', value.Split('.').Select(part => part.Trim().Trim('"', '\'')));
    }

    private static string StripComment(string value)
    {
        var single = false;
        var quoted = false;
        var escaped = false;
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (escaped) { escaped = false; continue; }
            if (current == '\\' && quoted) { escaped = true; continue; }
            if (current == '\'' && !quoted) { single = !single; continue; }
            if (current == '"' && !single) { quoted = !quoted; continue; }
            if (current == '#' && !single && !quoted) return value[..index];
        }
        return value;
    }
}
