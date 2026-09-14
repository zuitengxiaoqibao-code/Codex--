# 极简备份向导与官方旧配置审计设计

## 目标

让不熟悉 Codex 文件结构的用户完成备份时只需要理解三件事：先扫描、接受推荐范围、选择保存位置。同时依据当前 OpenAI 官方配置参考识别已不支持、已弃用和仍兼容的旧别名，避免把没有官方证据的文件误判为可删除垃圾。

## 默认流程

备份页继续保留四阶段结构，但默认只显示每阶段的一项主操作和一句结论。

1. “一键扫描并推荐”使用当前 Windows 用户目录，并自动读取 `CODEX_HOME`、`CODEX_SQLITE_HOME`、配置引用和任意本地盘符。自定义用户目录与额外位置进入“高级扫描设置”。
2. 默认使用完整迁移。扫描后显示一条面向普通用户的结论、个人数据数量和官方旧配置数量。抢救模式、密码保护和完整性明细进入“高级备份设置”。
3. 默认推荐项在扫描时自动选好。会话和类型页签放入“我要自己调整”折叠区；展开后仍保留全部筛选与逐项选择能力。
4. 用户选择保存位置并开始备份。开始备份仍会重新执行完整检查。

## 官方旧配置审计

审计器读取已经加载到内存的 TOML 语句，不增加复选框时的磁盘工作。按官方措辞分为：

- 已不支持：`approval_policy = "untrusted"`。
- 已弃用：`approval_policy = "on-failure"`、`features.codex_hooks`、`features.web_search`、`features.web_search_cached`、`features.web_search_request`。
- 旧名称或旧别名：`agents.max_threads`、`background_terminal_timeout`、`experimental_use_unified_exec_tool`、`memories.no_memories_if_mcp_or_web_search`、`shell_environment_policy.exclude`、`shell_environment_policy.include_only`。

每项结果包含旧键、官方建议替代项和配置文件位置。审计只读，不改写、不删除。原配置仍纳入备份。`managed_config.toml`、`config.yaml` 和 `config.yml` 仅标为“当前官方配置参考未确认”，不能标为已废弃。

官方依据：[OpenAI Codex Configuration Reference](https://learn.chatgpt.com/docs/config-file/config-reference)。

## 安全边界

- 只有官方页面明确使用 unsupported、deprecated、legacy 或 replaces the older 的项目才进入对应类别。
- 未知配置、旧文件名和用户自定义文件继续保存。
- 删除和隔离功能不因本审计自动选择任何项目。
- 审计结果不阻止备份；已不支持配置以醒目提醒呈现，建议用户在新系统启用前修改。

## 验证

- 单元测试覆盖全局键、表内键、点号键、注释、近似名称和未确认文件名。
- WPF smoke 验证极简入口、高级折叠区、手动选择折叠区及官方审计摘要控件存在。
- 发布 EXE 运行 self-test、WPF smoke、真实只读扫描和 PowerShell 5.1 发布校验。
