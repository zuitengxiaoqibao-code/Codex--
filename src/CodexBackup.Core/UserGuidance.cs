namespace CodexBackup.Core;

public static class UserGuidance
{
    public static string Explain(Finding finding)
    {
        var code = finding.Code;
        var location = string.IsNullOrWhiteSpace(finding.Path) ? "" : "\n位置：" + finding.Path;
        if (code.Contains("missing")) return "找不到原来的文件\n影响：对应项目或会话可能无法恢复，不能把缺失内容算作已备份。\n处理：接上原硬盘，或在来源列表选中该项，点击“定位已搬走的文件”。" + location;
        if (code == "active-writers" || code.Contains("wal")) return "Codex 还在使用数据\n影响：刚产生的会话或项目记录可能尚未保存完整。\n处理：正常退出 Codex、终端任务和桥接工具后，点击“重新扫描”。本工具不会替你强制关闭程序。" + location;
        if (code == "coverage-boundary") return "还有需要单独确认的内容\n网络电脑、云端任务、WSL、Docker 和其他 Windows 用户不属于本机目录扫描。若你使用过它们，请先导出到本地，再添加到备份。";
        if (code.StartsWith("msix-") || code == "codex-version-unknown") return "部分安装信息暂未确认\n影响：不等于会话丢失，但不能据此确认新旧版本兼容。\n处理：以扫描找到的数据位置为准，恢复前安装 Codex；安装信息可在详细记录中核对。" + location;
        if (code.StartsWith("complete-")) return "完整迁移暂不能继续\n" + finding.Message + location;
        if (code.Contains("unknown") || code.Contains("incomplete") || code.Contains("unreadable") || code.Contains("limit") || code.Contains("invalid"))
            return "有内容尚未查清\n影响：部分会话或项目可能漏掉，完整迁移会暂时停止。\n处理：确认原目录可访问，退出相关程序后重新扫描；也可以添加实际数据位置。请勿在问题未解决前清空旧磁盘。" + location;
        return (finding.Level == FindingLevel.Blocker ? "需要先处理：" : finding.Level == FindingLevel.Warning ? "请留意：" : "说明：") + finding.Message + location;
    }

    public static string Error(Exception error) => error switch
    {
        BackupException => error.Message,
        UnauthorizedAccessException => "没有权限读取或写入这个位置。\n原数据未被当作成功备份。请检查文件夹权限或改选你可以访问的目录，再重试。",
        IOException => "文件操作没有完成。\n可能是文件正在使用、空间不够或磁盘断开。请检查磁盘连接和剩余空间，关闭相关程序后重试。\n如果正在恢复，请保留回滚日志和旁边的辅助目录。",
        _ => "操作未完成，不能把当前结果当作成功。\n请保留现有备份和恢复日志，重新打开程序后使用“检查”。若仍失败，请保存详细记录供排查。"
    };
}
