namespace CodexBackup.Core;

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
    public string? SourcePath { get; set; }
}
