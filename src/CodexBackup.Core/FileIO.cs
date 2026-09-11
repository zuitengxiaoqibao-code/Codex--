using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace CodexBackup.Core;

internal static class FileIO
{
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true, MaxDepth = 48 };

    internal static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        PathSafety.RejectReparseAncestors(path);
        using var lease = DirectoryLease.Acquire(Path.GetDirectoryName(path)!);
        await using var stream = OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
    }

    internal static FileStream OpenRead(string path)
    {
        var handle = DirectoryLease.CreateFileW(DirectoryLease.Extended(path), 0x80000000, 1, IntPtr.Zero, 3, 0x48200000, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw new IOException($"不能只读打开文件（{Marshal.GetLastWin32Error()}）：{path}"); }
        var stream = new FileStream(handle, FileAccess.Read, 128 * 1024, isAsync:true);
        if (!GetFileInformationByHandle(stream.SafeFileHandle, out var info) || (info.Attributes & (uint)FileAttributes.ReparsePoint) != 0)
        { stream.Dispose(); throw new BackupException("文件在打开时发生变化或无法确认真实类型。"); }
        return stream;
    }

    internal static async Task CopyVerifiedAsync(string source, string destination, string? expectedHash, CancellationToken ct)
    {
        PathSafety.RejectReparseAncestors(source);
        PathSafety.RejectReparseAncestors(destination);
        using var sourceLease = DirectoryLease.Acquire(Path.GetDirectoryName(source)!);
        using var destinationLease = DirectoryLease.Acquire(Path.GetDirectoryName(destination)!);
        await using (var input = OpenRead(source))
        await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await input.CopyToAsync(output, ct);
            await output.FlushAsync(ct);
            output.Flush(true);
        }
        if (expectedHash is not null && !string.Equals(await HashAsync(destination, ct), expectedHash, StringComparison.Ordinal))
            throw new BackupException("复制后校验不一致，任务未完成。");
    }

    internal static void WriteJsonDurable<T>(string path, T value)
    {
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        PathSafety.RejectReparseAncestors(path);
        using var lease = DirectoryLease.Acquire(Path.GetDirectoryName(path)!);
        using (var output = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { JsonSerializer.Serialize(output, value, Json); output.Flush(true); }
        File.Move(tmp, path, true);
    }

    internal static T ReadJson<T>(string path, long maxBytes = 16 * 1024 * 1024)
    {
        PathSafety.RejectReparseAncestors(path);
        if (!File.Exists(path) || new FileInfo(path).Length > maxBytes) throw new BackupException("备份缺少清单，或清单超过安全大小上限。");
        using var lease = DirectoryLease.Acquire(Path.GetDirectoryName(path)!);
        using var input = OpenRead(path);
        try { return JsonSerializer.Deserialize<T>(input, Json) ?? throw new BackupException("清单为空。"); }
        catch (JsonException) { throw new BackupException("清单格式损坏。"); }
    }

    internal static void ValidateSourceType(string path)
    {
        PathSafety.RejectReparseAncestors(path);
        var attrs = File.GetAttributes(path);
        if ((attrs & (FileAttributes.Encrypted | FileAttributes.Offline | FileAttributes.SparseFile)) != 0)
            throw new BackupException($"文件属于 EFS 加密、离线或稀疏类型，本版不能保证完整恢复：{path}");
        var find = FindFirstStreamW(DirectoryLease.Extended(path), 0, out var data, 0);
        if (find == new IntPtr(-1))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 38) return;
            throw new BackupException($"无法枚举文件备用数据流（{error}）：{path}");
        }
        try
        {
            do
            {
                if (data.StreamName != "::$DATA") throw new BackupException($"检测到备用数据流 {data.StreamName}，本版保守阻止遗漏；请使用完整卷备份或单独处理：{path}");
            } while (FindNextStreamW(find, out data));
            if (Marshal.GetLastWin32Error() != 38) throw new BackupException("备用数据流枚举失败。");
        }
        finally { FindClose(find); }
    }

    [StructLayout(LayoutKind.Sequential)] internal struct ByHandleInfo
    { public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write; public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StreamData
    { public long StreamSize; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)] public string StreamName; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleInfo info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr FindFirstStreamW(string path, int info, out StreamData data, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool FindNextStreamW(IntPtr find, out StreamData data);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FindClose(IntPtr find);
}
