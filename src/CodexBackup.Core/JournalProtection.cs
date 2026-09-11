using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexBackup.Core;

internal sealed class JournalEnvelope
{
    public string Payload { get; set; } = "";
    public string Seal { get; set; } = "";
}

internal static class JournalProtection
{
    internal static void ValidateSize(JournalEnvelope envelope, long maximum = 255L * 1024 * 1024)
    {
        // Leave a 1 MiB reserve below the reader limit for subsequent state transitions.
        if (JsonSerializer.SerializeToUtf8Bytes(envelope, FileIO.Json).LongLength > maximum)
            throw new BackupException("回滚日志超过安全大小上限，请减少本次恢复范围；尚未开始的新替换不会执行。");
    }
    internal static JournalEnvelope Seal(RestoreJournal journal)
    {
        var payload = JsonSerializer.Serialize(journal, FileIO.Json);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return new() { Payload = payload, Seal = Convert.ToBase64String(Protect(digest, true)) };
    }
    internal static RestoreJournal Open(JournalEnvelope envelope)
    {
        try
        {
            var expected = Protect(Convert.FromBase64String(envelope.Seal), false);
            var actual = SHA256.HashData(Encoding.UTF8.GetBytes(envelope.Payload));
            if (!CryptographicOperations.FixedTimeEquals(expected, actual)) throw new BackupException("回滚日志被修改，无法自动执行。");
            return JsonSerializer.Deserialize<RestoreJournal>(envelope.Payload, FileIO.Json) ?? throw new BackupException("回滚日志为空。");
        }
        catch (Exception ex) when (ex is FormatException or JsonException or CryptographicException)
        { throw new BackupException("回滚日志损坏，或不属于此 Windows 用户。原始回滚副本仍可手动取回。"); }
    }
    private static byte[] Protect(byte[] data, bool protect)
    {
        var input = new Blob { Size = data.Length, Data = Marshal.AllocHGlobal(data.Length) };
        Marshal.Copy(data, 0, input.Data, data.Length);
        try
        {
            Blob output;
            var ok = protect ? CryptProtectData(ref input, "CodexBackup restore journal", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new CryptographicException("Windows 数据保护操作失败。");
            try { var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, output.Size); return result; }
            finally { LocalFree(output.Data); }
        }
        finally { Marshal.FreeHGlobal(input.Data); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CryptProtectData(ref Blob input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
