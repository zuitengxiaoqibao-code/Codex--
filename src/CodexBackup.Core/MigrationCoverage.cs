namespace CodexBackup.Core;

/// <summary>Completeness is separate from file hashes and never inferred for legacy packages.</summary>
public static class MigrationCoverage
{
    public static string Canonical(string path)
    {
        var value = path.Replace('/', '\\');
        if (value.StartsWith(@"\\?\", StringComparison.Ordinal) && value.Length > 6 && char.IsAsciiLetter(value[4]) && value[5] == ':') value = value[4..];
        return PathSafety.Full(value);
    }

    public static string Resolve(string path, IReadOnlyDictionary<string,string> replacements)
    {
        var original = Canonical(path);
        foreach (var pair in replacements.OrderByDescending(p => p.Key.Length))
        {
            var key = Canonical(pair.Key);
            if (PathSafety.Contains(key, original)) return Canonical(Canonical(pair.Value) + original[key.Length..]);
        }
        return original;
    }

    public static List<Finding> Evaluate(BackupRequest request)
    {
        var result = new List<Finding>();
        if (!request.CompleteMigration) return result;
        if (request.DiscoveredSessionCount > request.Sessions.Count)
            result.Add(new(FindingLevel.Blocker, "complete-session-selection", $"当前完整迁移只选择了 {request.Sessions.Count} / {request.DiscoveredSessionCount} 条会话关联。请在会话列表中全选需要保留的会话；如果只想抢救部分会话，请切换到自选 / 抢救模式。"));
        var selected = request.Sources.Where(s => s.Selected && (Directory.Exists(s.Path) || File.Exists(s.Path))).ToList();
        foreach (var core in selected.Where(s => s.Kind == SourceKind.Core))
            foreach (var parent in selected.Where(s => s.IsDirectory && !Canonical(s.Path).Equals(Canonical(core.Path), StringComparison.OrdinalIgnoreCase) && PathSafety.Contains(s.Path, core.Path)))
                result.Add(new(FindingLevel.Blocker, "complete-nested-core", "这个项目位置包含了整个 Codex 数据目录，无法作为普通项目迁移。请把项目定位到实际源码文件夹，避免选择整个用户目录；如只需保留原始数据，可使用自选 / 抢救模式。", parent.Path));
        if (!selected.Any(s => s.Kind == SourceKind.Core)) result.Add(new(FindingLevel.Blocker, "complete-core-missing", "还没有找到会话主目录。请点击“添加其他数据位置”，选择你实际使用的 Codex 数据目录后重新扫描。"));
        bool Covered(string path)
        {
            try { var resolved = Resolve(path, request.PathReplacements); return selected.Any(s => Directory.Exists(s.Path) ? PathSafety.Contains(s.Path, resolved) : Canonical(s.Path).Equals(resolved, StringComparison.OrdinalIgnoreCase)); }
            catch (Exception ex) when (ex is BackupException or ArgumentException) { return false; }
        }
        void Require(string path, string description)
        {
            try
            {
                var resolved = Resolve(path, request.PathReplacements);
                if (!File.Exists(resolved) && !Directory.Exists(resolved)) result.Add(new(FindingLevel.Blocker, "complete-file-missing", $"{description}的原文件找不到。只有会话记录不能还原项目源码；请选择该项并点击“定位已搬走的文件”，或连接原来的硬盘。", path));
                else if (!Covered(path)) result.Add(new(FindingLevel.Blocker, "complete-not-selected", $"{description}没有加入备份。完整迁移必须同时保存会话和相关文件，请重新勾选或选择“完整迁移”。", path));
            }
            catch (Exception ex) when (ex is BackupException or ArgumentException) { result.Add(new(FindingLevel.Blocker, "complete-path-unsupported", $"{description}的位置无法自动备份。请先将网络、WSL 或其他设备上的内容导出到本地目录，再使用“定位已搬走的文件”关联。", path)); }
        }
        foreach (var source in request.Sources.Where(s => !(s.Kind == SourceKind.Core && !s.Exists && !s.Required) && (s.Kind is SourceKind.Project or SourceKind.Core or SourceKind.Session or SourceKind.Memory || s.Required)))
            Require(source.Path, source.Kind == SourceKind.Project ? "项目“" + source.Name + "”" : source.Kind == SourceKind.Session ? "会话文件" : source.Name);
        foreach (var session in request.Sessions)
        {
            if (string.IsNullOrWhiteSpace(session.ProjectPath) || string.IsNullOrWhiteSpace(session.TranscriptPath))
                result.Add(new(FindingLevel.Blocker, "complete-session-unknown", $"会话 {session.Id} 的源码位置或对话文件尚未确认。请重新扫描实际数据目录，不能将当前结果当作完整迁移。"));
            else { Require(session.ProjectPath, "会话 " + session.Id + " 对应的项目"); Require(session.TranscriptPath, "会话 " + session.Id + " 的对话文件"); }
        }
        foreach (var finding in request.DiscoveryFindings)
        {
            if (finding.Code is "known-location-missing" or "active-writers" or "coverage-boundary" or "codex-version-unknown" || finding.Code.StartsWith("msix-")) continue;
            if (finding.Code is "referenced-project-missing" or "referenced-memory-missing" or "required-codex-root-missing" or "session-file-missing" or "session-transcript-missing" or "configured-session-missing" or "gitdir-missing" or "git-commondir-missing")
            {
                if (!string.IsNullOrWhiteSpace(finding.Path) && Covered(finding.Path)) continue;
            }
            if (finding.Level == FindingLevel.Blocker || finding.Code.Contains("unsupported") || finding.Code.Contains("missing") || finding.Code.Contains("truncated") || finding.Code.Contains("incomplete") || finding.Code.Contains("unknown") || finding.Code.Contains("unreadable") || finding.Code.Contains("uncertain") || finding.Code.Contains("limit") || finding.Code.Contains("invalid"))
                result.Add(new(FindingLevel.Blocker, "complete-discovery-incomplete", "有一部分会话或配置还没有读完整，暂时不能制作完整迁移包。请先正常退出 Codex 和相关工具，再重新扫描；如仍有问题，请查看此项原始位置并补充数据。", finding.Path));
        }
        return result.DistinctBy(f => (f.Code, f.Path, f.Message)).ToList();
    }

    public static void ValidateInventory(BackupManifest manifest, IReadOnlyList<FileRecord> records)
    {
        if (!manifest.CompleteMigration) return;
        if (manifest.Sessions is null || manifest.LogicalSources is null || manifest.PathReplacements is null || manifest.Sessions.Count > 100000 || manifest.LogicalSources.Count > 20000 || manifest.PathReplacements.Count > 10000)
            throw new BackupException("完整迁移清单缺失或过大，不能确认会话与项目关联。");
        if (!manifest.LogicalSources.Any(s => s.Kind == SourceKind.Core && s.Selected)) throw new BackupException("完整迁移包缺少会话主目录的来源记录。");
        foreach (var session in manifest.Sessions)
        {
            if (!ContainsPath(manifest, records, session.ProjectPath, true) || !ContainsPath(manifest, records, session.TranscriptPath, false))
                throw new BackupException($"会话 {session.Id} 没有同时包含项目目录和对话文件。此包不能认定为完整迁移。");
        }
        foreach (var source in manifest.LogicalSources.Where(s => !(s.Kind == SourceKind.Core && !s.Exists && !s.Required) && (s.Kind is SourceKind.Project or SourceKind.Core or SourceKind.Session or SourceKind.Memory || s.Required)))
            if (!ContainsPath(manifest, records, source.Path, source.IsDirectory)) throw new BackupException($"完整迁移包缺少关联文件：{source.Name}。请回到原系统重新备份。");
    }

    public static bool ContainsPath(BackupManifest manifest, IReadOnlyList<FileRecord> records, string path, bool directory)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var resolved = Resolve(path, manifest.PathReplacements);
        foreach (var root in manifest.Roots)
        {
            var original = Canonical(root.OriginalPath);
            if (!PathSafety.Contains(original, resolved)) continue;
            var relative = resolved.Equals(original, StringComparison.OrdinalIgnoreCase) ? "" : resolved[(original.Length + 1)..];
            if (records.Any(r => r.RootId == root.Id && r.RelativePath.Replace('/', '\\').Equals(relative, StringComparison.OrdinalIgnoreCase) && r.IsDirectory == directory)) return true;
        }
        return false;
    }
}
