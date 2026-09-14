namespace CodexBackup.Core;

public static class UserGuidance
{
    public static string ExplainException(Exception error)
    {
        var message = Sanitize(error.Message);
        return error switch
        {
            UnauthorizedAccessException => $"原因：当前用户没有这个位置的读取或写入权限。\n影响：本次结果不能算作成功，原数据不会被当作已备份。\n处理：改选你有权限的目录，或关闭占用程序后重试。{message}",
            IOException => $"原因：文件正在使用、磁盘空间不足、磁盘断开，或文件在操作期间发生变化。\n影响：备份或恢复可能不完整，不能清空旧系统。\n处理：关闭 Codex、终端任务和外围工具，确认磁盘空间与连接后重试；恢复失败时保留日志和辅助目录。{message}",
            System.Security.Cryptography.CryptographicException => $"原因：密码不正确，或加密备份内容已经损坏。\n影响：无法安全读取备份，不能继续恢复。\n处理：重新输入创建备份时的密码；如果密码遗失，只能使用另一份备份。",
            ArgumentException => $"原因：路径或选项格式不正确。\n影响：目标位置没有被修改。\n处理：选择有效的本地 NTFS 文件夹，并重新预演。{message}",
            BackupException => $"原因：{message}\n影响：当前步骤没有被当作成功，必要时仍可使用检查或回滚。\n处理：按上面的说明处理后重试；不要删除原数据或恢复辅助目录。",
            _ => $"原因：程序无法确认这一步是否安全完成。\n影响：当前结果不能算作成功。\n处理：保留现有备份和日志，打开“检查”查看详细记录后重试。{message}"
        };
    }

    public static string Explain(Finding finding)
    {
        var code = finding.Code;
        var location = string.IsNullOrWhiteSpace(finding.Path) ? "" : "\n位置：" + finding.Path;
        if (code.StartsWith("configured-sqlite-home", StringComparison.Ordinal))
            return "Codex 的会话索引在单独的 SQLite 状态目录中\n影响：只保存默认 .codex 文件夹会漏掉会话索引，恢复后可能看不到历史会话。\n处理：确认这个目录仍在原硬盘上；如果目录已搬走，请定位实际目录后重新扫描。" + location;
        if (code == "configured-log-dir-missing")
            return "Codex 指定的日志目录找不到\n影响：历史日志不能确认已随备份保存，但不会替代会话正文。\n处理：确认原硬盘已连接；如果不需要日志，可在自选 / 抢救模式中继续保存其他内容。" + location;
        if (code == "configured-external-path-missing")
            return "配置引用了一个外部文件或目录，但现在找不到\n影响：依赖这个文件的模型指令或运行设置，恢复后可能需要重新定位。\n处理：接回原硬盘或使用“定位已搬走的文件”选择它现在的位置。" + location;
        if (code.Contains("missing")) return "找不到原来的文件\n影响：对应项目或会话可能无法恢复，不能把缺失内容算作已备份。\n处理：接上原硬盘，或在来源列表选中该项，点击“定位已搬走的文件”。" + location;
        if (code == "active-writers" || code.Contains("wal")) return "Codex 还在使用数据\n影响：刚产生的会话或项目记录可能尚未保存完整。\n处理：正常退出 Codex、终端任务和桥接工具后，点击“重新扫描”。本工具不会替你强制关闭程序。" + location;
        if (code == "coverage-boundary") return "还有需要单独确认的内容\n网络电脑、云端任务、WSL、Docker 和其他 Windows 用户不属于本机目录扫描。若你使用过它们，请先导出到本地，再添加到备份。";
        if (code == "official-unsupported-config")
            return "发现当前 Codex 已不支持的旧设置\n影响：原配置仍会原样备份，但新系统启用前必须按官方建议修改，否则相关功能可能无法使用。\n处理：展开“官方旧配置”查看旧项和替代项；本工具只提示，不会自动改写或删除。" + location;
        if (code is "official-deprecated-config" or "official-legacy-config")
            return "发现官方已弃用或已更名的旧设置\n影响：原配置仍会原样备份；以后版本可能不再兼容。\n处理：按提示迁移到官方新名称。本工具不会自动改写，以免改变你的现有行为。" + location;
        if (code == "official-config-format-unconfirmed")
            return "发现官方配置参考没有确认的旧文件名\n影响：不能确认当前 Codex 是否仍读取它，也不能据此删除。\n处理：文件仍会保留并备份；恢复后先人工核对，再决定是否继续使用。" + location;
        if (code == "complete-session-selection") return "完整迁移还没有包含全部已发现会话\n影响：如果继续，备份包不能证明会话列表完整。\n处理：在会话列表中选择全部需要保留的活动和归档会话；只想保存其中一部分时，请切换到自选 / 抢救模式。" + location;
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

    private static string Sanitize(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "";
        foreach (var typeName in new[] { "IOException", "UnauthorizedAccessException", "InvalidDataException", "CryptographicException", "ArgumentException", "BackupException" })
            message = message.Replace(typeName, "文件操作错误", StringComparison.OrdinalIgnoreCase);
        return "\n技术信息：" + message;
    }
}
