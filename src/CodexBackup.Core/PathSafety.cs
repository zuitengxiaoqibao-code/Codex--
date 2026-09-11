using System.Text.RegularExpressions;

namespace CodexBackup.Core;

public static class PathSafety
{
    public static string Full(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\") || path.Contains('\0'))
            throw new BackupException("仅支持完整的本地磁盘路径，不支持网络、设备或相对路径。");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (full.Length > 32000 || full.Length < 3 || full[1] != ':') throw new BackupException("路径不受支持。");
        if (full.Length > 3) ValidateRelative(full[3..]);
        return full;
    }

    public static bool Contains(string parent, string child) =>
        string.Equals(Full(parent), Full(child), StringComparison.OrdinalIgnoreCase) ||
        Full(child).StartsWith(Path.TrimEndingDirectorySeparator(Full(parent)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static void ValidateRelative(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Length > 32000)
            throw new BackupException("备份条目包含无效的相对路径。");
        foreach (var part in relative.Split(['/', '\\']))
        {
            if (part.Length == 0 || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.') ||
                part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || part.Contains(':') ||
                Regex.IsMatch(part, @"^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                throw new BackupException("条目包含路径穿越、设备名或不受支持的文件名。");
        }
    }

    public static string Under(string root, string relative)
    {
        ValidateRelative(relative);
        var path = Full(Path.Combine(root, relative));
        if (!Contains(root, path) || string.Equals(Full(root), path, StringComparison.OrdinalIgnoreCase))
            throw new BackupException("路径越过所选目录。");
        RejectReparseAncestors(path);
        return path;
    }

    public static void RejectReparseAncestors(string path)
    {
        var current = Full(path);
        while (true)
        {
            try
            {
                var attrs = File.GetAttributes(current);
                if ((attrs & FileAttributes.ReparsePoint) != 0) throw new BackupException($"路径包含链接、挂载点或云占位符，需先定位真实目录：{current}");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current) break;
            current = parent;
        }
    }

    public static void RequireNtfs(string path)
    {
        var drive = new DriveInfo(Path.GetPathRoot(Full(path))!);
        if (!drive.IsReady || !drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
            throw new BackupException("此版本仅向就绪的本地 NTFS 卷写入备份或恢复数据。请改选 NTFS 磁盘。");
    }

    public static void RejectSystemTarget(string path)
    {
        var full = Full(path);
        var blocked = new[] { Path.GetPathRoot(full)!, Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
        if (blocked.Any(b => !string.IsNullOrEmpty(b) && string.Equals(Full(b), full, StringComparison.OrdinalIgnoreCase)))
            throw new BackupException("不能替换磁盘根目录、Windows、Program Files 或整个用户目录。");
        foreach (var systemRoot in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            if (!string.IsNullOrEmpty(systemRoot) && Contains(systemRoot, full)) throw new BackupException("不能恢复到 Windows 或 Program Files 系统目录内部。");
    }
}
