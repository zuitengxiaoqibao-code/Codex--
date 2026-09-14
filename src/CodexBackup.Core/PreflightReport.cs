namespace CodexBackup.Core;

public enum PreflightStatus { Ready, Blocked, SalvageOnly }

public sealed class PreflightReport
{
    public PreflightStatus Status { get; set; }
    public string Summary { get; set; } = "";
    public bool CanReinstall => Status == PreflightStatus.Ready;
    public int UniqueSessionCount { get; set; }
    public int SessionAssociationCount { get; set; }
    public int ProjectLocationCount { get; set; }
    public List<Finding> Findings { get; set; } = [];

    public static PreflightReport Build(ScanResult scan, BackupRequest request, IReadOnlyList<Finding>? evaluatedCoverage = null)
    {
        var gaps = request.CompleteMigration ? evaluatedCoverage?.ToList() ?? MigrationCoverage.Evaluate(request) : [];
        var status = request.CompleteMigration
            ? gaps.Count == 0 ? PreflightStatus.Ready : PreflightStatus.Blocked
            : PreflightStatus.SalvageOnly;
        var summary = status switch
        {
            PreflightStatus.Ready => "已覆盖本次扫描发现的会话、项目和必需关联，可以进入重装前最后备份。",
            PreflightStatus.Blocked => $"还有 {gaps.Count} 项必须处理，当前不能清空旧系统。",
            _ => "这是自选/抢救范围，只保存已选择且可读取的内容，不能作为完整迁移保障。"
        };
        return new PreflightReport
        {
            Status = status,
            Summary = summary,
            UniqueSessionCount = scan.UniqueSessionCount,
            SessionAssociationCount = scan.SessionAssociationCount,
            ProjectLocationCount = scan.ProjectLocationCount,
            Findings = scan.Findings.Concat(gaps).DistinctBy(f => (f.Code, f.Path, f.Message)).ToList()
        };
    }
}
