using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace CodexBackup.Core;

public static class WindowsEnvironment
{
    public sealed record InstalledPackage(string Name, string Version, string InstallLocation);

    public static IReadOnlyList<InstalledPackage> GetInstalledCodexMsixPackages(CancellationToken cancellationToken, out string? warning)
    {
        warning = null;
        if (!OperatingSystem.IsWindows()) return [];
        const int outputLimit = 1024 * 1024;
        var executable = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(executable)) { warning = "未找到受信任的系统 PowerShell，无法读取商店版 Codex 安装信息。"; return []; }
        using var process = new Process { StartInfo = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        process.StartInfo.ArgumentList.Add("-NoLogo");
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-NonInteractive");
        process.StartInfo.ArgumentList.Add("-Command");
        process.StartInfo.ArgumentList.Add("ConvertTo-Json -Compress -InputObject @(Get-AppxPackage -Name '*Codex*' | Select-Object Name,Version,InstallLocation)");
        var output = new StringBuilder(); var overflow = false;
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (output)
            {
                if (output.Length + e.Data.Length + 1 > outputLimit) overflow = true;
                else output.AppendLine(e.Data);
            }
        };
        try
        {
            if (!process.Start()) { warning = "无法启动受信任的系统包查询。"; return []; }
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            var deadline = Stopwatch.StartNew();
            while (!process.WaitForExit(100))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (overflow || deadline.Elapsed >= TimeSpan.FromSeconds(10))
                {
                    try { process.Kill(true); } catch { }
                    warning = overflow ? "商店包查询输出超过 1 MiB 上限，结果已丢弃。" : "商店包查询超过 10 秒，结果已丢弃。";
                    return [];
                }
            }
            process.WaitForExit();
            if (process.ExitCode != 0) { warning = "系统商店包查询失败，无法确认 Codex 安装位置和版本。"; return []; }
            string json; lock (output) json = output.ToString();
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json, new JsonDocumentOptions { MaxDepth = 8 });
            var elements = document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement.EnumerateArray().ToList() : [document.RootElement];
            var result = new List<InstalledPackage>();
            foreach (var element in elements.Take(100))
            {
                var name = element.TryGetProperty("Name", out var n) ? n.GetString() : null;
                var version = element.TryGetProperty("Version", out var v) ? v.ToString() : null;
                var location = element.TryGetProperty("InstallLocation", out var l) ? l.GetString() : null;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(location) || !Path.IsPathFullyQualified(location)) continue;
                result.Add(new(name, version, Path.GetFullPath(location)));
            }
            return result;
        }
        catch (OperationCanceledException) { try { if (!process.HasExited) process.Kill(true); } catch { } throw; }
        catch (Exception ex) { warning = $"读取商店包元数据失败（{ex.GetType().Name}）。"; return []; }
    }

    public static IReadOnlyList<string> GetInstalledLocations(string profile)
    {
        var candidates = new List<string>();
        Add(candidates, Path.Combine(profile, "AppData", "Local", "Programs", "Codex"));
        Add(candidates, Path.Combine(profile, "AppData", "Local", "Codex"));
        Add(candidates, Path.Combine(profile, "AppData", "Roaming", "Codex"));
        Add(candidates, Path.Combine(profile, "AppData", "Roaming", "Codex++"));

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles)) Add(candidates, Path.Combine(programFiles, "Codex"));
        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static IReadOnlyList<string> GetWriterNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    if (process.Id == Environment.ProcessId) continue;
                    var name = process.ProcessName;
                    if (name.Contains("codex", StringComparison.OrdinalIgnoreCase)) names.Add(name);
                }
            }
        }
        catch { }
        return names.Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string DescribeVolume(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrWhiteSpace(root)) return "卷未知";
            var drive = new DriveInfo(root);
            var free = drive.IsReady ? drive.AvailableFreeSpace : 0;
            var disks = GetDiskNumbers(path);
            return $"卷 {drive.Name}；类型 {drive.DriveType}；可用空间 {free} 字节；磁盘编号 {(disks.Count == 0 ? "未知" : string.Join(",", disks))}";
        }
        catch { return "卷未知"; }
    }

    public static IReadOnlyList<int> GetDiskNumbers(string path)
    {
        if (!OperatingSystem.IsWindows()) return [];
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrWhiteSpace(root) || root.Length < 2 || root[1] != ':') return [];
            using var handle = CreateFile($@"\\.\{root[..2]}", 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (handle.IsInvalid) return [];
            const int capacity = 64 * 1024;
            var buffer = Marshal.AllocHGlobal(capacity);
            try
            {
                if (!DeviceIoControl(handle, 0x00560000, IntPtr.Zero, 0, buffer, capacity, out var returned, IntPtr.Zero) || returned < 8) return [];
                var count = Math.Min(Marshal.ReadInt32(buffer), 256);
                var extentOffset = Marshal.OffsetOf<VolumeDiskExtents>(nameof(VolumeDiskExtents.FirstExtent)).ToInt32();
                var extentSize = Marshal.SizeOf<DiskExtent>();
                var result = new HashSet<int>();
                for (var i = 0; i < count && extentOffset + (i + 1) * extentSize <= returned; i++)
                    result.Add(Marshal.ReadInt32(buffer, extentOffset + i * extentSize));
                return result.Order().ToList();
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        catch { return []; }
    }

    [StructLayout(LayoutKind.Sequential)] private struct DiskExtent { public int DiskNumber; public long StartingOffset; public long ExtentLength; }
    [StructLayout(LayoutKind.Sequential)] private struct VolumeDiskExtents { public int NumberOfDiskExtents; public DiskExtent FirstExtent; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint controlCode, IntPtr input, int inputSize, IntPtr output, int outputSize, out int returned, IntPtr overlapped);

    private static void Add(List<string> paths, string path)
    {
        try { paths.Add(Path.GetFullPath(path)); } catch { }
    }
}
