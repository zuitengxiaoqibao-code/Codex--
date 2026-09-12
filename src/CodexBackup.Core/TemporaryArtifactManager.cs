namespace CodexBackup.Core;

public sealed record TemporaryArtifact(string Path, string Kind, bool IsDirectory);

public static class TemporaryArtifactManager
{
    private static readonly string[] Suffixes = [".incomplete", ".stage", ".rollback", ".original", ".undo", ".journal.json"];

    public static IReadOnlyList<TemporaryArtifact> List(string root)
    {
        var location = PathSafety.Full(root);
        if (!Directory.Exists(location)) return [];
        var result = new List<TemporaryArtifact>();
        var queue = new Queue<string>([location]);
        var visited = 0;
        while (queue.Count > 0 && visited++ < 10000)
        {
            var current = queue.Dequeue();
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(current, "*", SearchOption.TopDirectoryOnly).ToList(); }
            catch { continue; }
            foreach (var path in entries)
            {
                if (IsArtifact(path)) { result.Add(new(path, Kind(path), Directory.Exists(path))); continue; }
                if (!Directory.Exists(path)) continue;
                try { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) queue.Enqueue(path); } catch { }
            }
        }
        return result;
    }

    public static void ValidateForCleanup(string path, bool verified)
    {
        var target = PathSafety.Full(path);
        if (!verified) throw new BackupException("临时目录尚未通过验收，不能自动删除；请先完成检查并保留回滚副本。");
        if (!IsArtifact(target)) throw new BackupException("指定位置不是受保护的恢复临时目录，拒绝删除。");
        PathSafety.RejectSystemTarget(target);
        PathSafety.RejectReparseAncestors(target);
    }

    public static void DeleteAfterVerification(string path, bool verified)
    {
        ValidateForCleanup(path, verified);
        var target = PathSafety.Full(path);
        if (Directory.Exists(target)) Directory.Delete(target, true);
        else if (File.Exists(target)) File.Delete(target);
    }

    private static bool IsArtifact(string path) => Suffixes.Any(s => path.EndsWith(s, StringComparison.OrdinalIgnoreCase)) || Path.GetFileName(path).Equals(".codex-encrypted-temp", StringComparison.OrdinalIgnoreCase);
    private static string Kind(string path) => Path.GetFileName(path).Equals(".codex-encrypted-temp", StringComparison.OrdinalIgnoreCase) ? "加密临时目录" : Suffixes.FirstOrDefault(s => path.EndsWith(s, StringComparison.OrdinalIgnoreCase))?.TrimStart('.') ?? "临时目录";
}
