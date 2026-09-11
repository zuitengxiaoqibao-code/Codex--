using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CodexBackup.Core;

// Keep each existing ancestor from being renamed/replaced while paths below it are accessed.
internal sealed class DirectoryLease : IDisposable
{
    private readonly List<SafeFileHandle> handles = [];
    internal static DirectoryLease Acquire(string directory)
    {
        var lease = new DirectoryLease();
        try
        {
            var full = PathSafety.Full(directory);
            var chain = new Stack<string>();
            for (var path = full; !string.IsNullOrEmpty(path); path = Path.GetDirectoryName(path)) chain.Push(path);
            foreach (var path in chain)
            {
                if (!Directory.Exists(path)) throw new BackupException("路径父目录不存在或在操作期间消失。");
                var handle = CreateFileW(Extended(path), 0x80, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                if (handle.IsInvalid) { handle.Dispose(); throw new BackupException($"无法固定目录身份，可能被占用或无权限（{Marshal.GetLastWin32Error()}）：{path}"); }
                lease.handles.Add(handle);
                if (!GetFileInformationByHandle(handle, out var info) || (info.Attributes & (uint)FileAttributes.ReparsePoint) != 0)
                    throw new BackupException("路径包含链接或无法确认真实目录身份。");
            }
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }
    internal static string Extended(string path) => path.StartsWith(@"\\?\") ? path : @"\\?\" + Path.GetFullPath(path);
    public void Dispose() { foreach (var handle in handles.AsEnumerable().Reverse()) handle.Dispose(); handles.Clear(); }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileIO.ByHandleInfo info);
}
