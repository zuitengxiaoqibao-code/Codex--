using System.Diagnostics;

namespace CodexBackup.Core;

public static class WriterGuard
{
    public static bool IsCodexHome(string path)
    {
        var homes = new List<string> { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex") };
        foreach (var scope in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
        {
            var home = Environment.GetEnvironmentVariable("CODEX_HOME", scope);
            if (!string.IsNullOrWhiteSpace(home) && Path.IsPathFullyQualified(home)) homes.Add(home);
        }
        return homes.Any(h => PathSafety.Full(h).Equals(PathSafety.Full(path), StringComparison.OrdinalIgnoreCase));
    }
    public static bool IsProtectedLocation(string path)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roots = new List<string> { Path.Combine(profile, ".codex"), Path.Combine(profile, ".cc-connect"), Path.Combine(profile, ".cc-switch"),
            Path.Combine(roaming, "Codex"), Path.Combine(roaming, "Codex++"), Path.Combine(local, "Codex") };
        foreach (var scope in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
        {
            var home = Environment.GetEnvironmentVariable("CODEX_HOME", scope);
            if (!string.IsNullOrWhiteSpace(home) && Path.IsPathFullyQualified(home)) roots.Add(home);
        }
        var packages = Path.Combine(local, "Packages");
        if (Directory.Exists(packages))
            roots.AddRange(Directory.EnumerateDirectories(packages, "*Codex*", SearchOption.TopDirectoryOnly));
        return roots.Any(root => PathSafety.Contains(root, path) || PathSafety.Contains(path, root));
    }

    internal static void RequireStoppedForSources(IEnumerable<SourceItem> sources)
    {
        var list = sources.ToList();
        if (list.Any(s => IsProtectedLocation(s.Path))) RequireStopped([SourceKind.Core]);
        else RequireStopped(list.Select(s => s.Kind));
    }
    public static List<string> RunningWriters()
    {
        var list = new List<string>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var name = process.ProcessName;
                    if (name.Equals("codex", StringComparison.OrdinalIgnoreCase) || name.Equals("codex++", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("cc-connect", StringComparison.OrdinalIgnoreCase) || name.Equals("cc-switch", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("CodexDesktop", StringComparison.OrdinalIgnoreCase)) list.Add($"{name} (PID {process.Id})");
                }
                catch (InvalidOperationException) { }
            }
        }
        return list;
    }
    internal static void RequireStopped(IEnumerable<SourceKind> kinds)
    {
        if (!kinds.Any(k => k is SourceKind.Core or SourceKind.Application or SourceKind.Tool)) return;
        var writers = RunningWriters();
        if (writers.Count != 0) throw new BackupException("请正常退出 Codex、CLI 和桥接工具后再执行。本工具不会强制结束任务。仍在运行：" + string.Join("；", writers));
    }
}
