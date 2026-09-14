using CodexBackup.Core;
using Xunit;

namespace CodexBackup.Tests;

public class OfficialConfigAuditTests
{
    [Fact]
    public void ReportsUnsupportedDeprecatedAndLegacySettingsWithReplacements()
    {
        var findings = OfficialConfigAudit.Inspect(
        [
            "approval_policy = 'untrusted'",
            "background_terminal_timeout = 60000",
            "experimental_use_unified_exec_tool = true",
            "[agents]",
            "max_threads = 6",
            "[features]",
            "codex_hooks = true",
            "web_search_cached = true",
            "[memories]",
            "no_memories_if_mcp_or_web_search = true",
            "[shell_environment_policy]",
            "include_only = ['PATH']"
        ],
        @"C:\profile\.codex\config.toml");

        Assert.Contains(findings, finding => finding.Code == "official-unsupported-config" && finding.Message.Contains("approval_policy") && finding.Message.Contains("on-request"));
        Assert.Contains(findings, finding => finding.Code == "official-deprecated-config" && finding.Message.Contains("features.codex_hooks") && finding.Message.Contains("features.hooks"));
        Assert.Contains(findings, finding => finding.Code == "official-deprecated-config" && finding.Message.Contains("features.web_search_cached") && finding.Message.Contains("web_search"));
        Assert.Contains(findings, finding => finding.Code == "official-legacy-config" && finding.Message.Contains("agents.max_threads") && finding.Message.Contains("agents.max_concurrent_threads_per_session"));
        Assert.Contains(findings, finding => finding.Code == "official-legacy-config" && finding.Message.Contains("background_terminal_timeout") && finding.Message.Contains("background_terminal_max_timeout"));
        Assert.Contains(findings, finding => finding.Code == "official-legacy-config" && finding.Message.Contains("experimental_use_unified_exec_tool") && finding.Message.Contains("features.unified_exec"));
        Assert.Contains(findings, finding => finding.Code == "official-legacy-config" && finding.Message.Contains("memories.no_memories_if_mcp_or_web_search") && finding.Message.Contains("memories.disable_on_external_context"));
        Assert.Contains(findings, finding => finding.Code == "official-legacy-config" && finding.Message.Contains("shell_environment_policy.include_only") && finding.Message.Contains("shell_environment_policy.filters"));
        Assert.All(findings, finding => Assert.Equal(@"C:\profile\.codex\config.toml", finding.Path));
    }

    [Fact]
    public void SupportsDottedKeysAndDoesNotMatchSimilarNames()
    {
        var findings = OfficialConfigAudit.Inspect(
        [
            "approval_policy = 'on-failure' # old policy",
            "features.web_search_request = true",
            "features.web_search_request_extra = true",
            "[projects.'C:\\work']",
            "trust_level = 'untrusted'"
        ],
        @"C:\profile\.codex\config.toml");

        Assert.Equal(2, findings.Count);
        Assert.Contains(findings, finding => finding.Code == "official-deprecated-config" && finding.Message.Contains("approval_policy"));
        Assert.Contains(findings, finding => finding.Code == "official-deprecated-config" && finding.Message.Contains("features.web_search_request"));
    }

    [Fact]
    public void DoesNotTreatDottedKeysInsideAnotherTableAsGlobalSettings()
    {
        var findings = OfficialConfigAudit.Inspect(
        [
            "[projects.'C:\\work.with.dots']",
            "features.web_search_request = true",
            "approval_policy = 'untrusted'",
            "[[agents.roles]]",
            "max_threads = 3"
        ],
        @"C:\profile\.codex\config.toml");

        Assert.Empty(findings);
    }

    [Fact]
    public void UserGuidanceExplainsThatOldSettingsRemainBackedUp()
    {
        var unsupported = UserGuidance.Explain(new(FindingLevel.Warning, "official-unsupported-config", "旧配置", @"C:\profile\.codex\config.toml"));
        var deprecated = UserGuidance.Explain(new(FindingLevel.Warning, "official-deprecated-config", "旧配置", @"C:\profile\.codex\config.toml"));
        var unconfirmed = UserGuidance.Explain(new(FindingLevel.Info, "official-config-format-unconfirmed", "未知格式", @"C:\profile\.codex\config.yaml"));

        Assert.Contains("新系统启用前", unsupported);
        Assert.Contains("仍会原样备份", unsupported);
        Assert.Contains("不会自动改写", deprecated);
        Assert.Contains("不能据此删除", unconfirmed);
    }

    [Theory]
    [InlineData("managed_config.toml")]
    [InlineData("config.yaml")]
    [InlineData("config.yml")]
    public void UndocumentedConfigFilenamesAreUnconfirmedRatherThanDeprecated(string fileName)
    {
        var findings = OfficialConfigAudit.Inspect([], Path.Combine(@"C:\profile\.codex", fileName));

        var finding = Assert.Single(findings);
        Assert.Equal("official-config-format-unconfirmed", finding.Code);
        Assert.DoesNotContain("已废弃", finding.Message);
        Assert.Contains("没有确认", finding.Message);
    }
}
