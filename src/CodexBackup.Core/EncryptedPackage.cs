using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace CodexBackup.Core;

/// <summary>Authenticated, chunked encryption envelope for an ordinary package directory.</summary>
public static class EncryptedPackage
{
    private static readonly byte[] Magic = "CDBXENC1"u8.ToArray();
    private const byte Version = 1;
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int ChunkSize = 1024 * 1024;
    private const int Iterations = 600_000;
    private const int HeaderSize = 8 + 1 + 4 + 4 + SaltSize;

    public static bool IsEncryptedFile(string path) => File.Exists(path) && Path.GetExtension(path).Equals(".codexenc", StringComparison.OrdinalIgnoreCase);

    public static async Task<string> CreateAsync(string sourceDirectory, string destinationFile, string password, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ValidatePassword(password);
        var source = PathSafety.Full(sourceDirectory);
        if (!Directory.Exists(source)) throw new BackupException("加密来源目录不存在。");
        var destination = PathSafety.Full(destinationFile);
        PathSafety.RejectReparseAncestors(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var tempRoot = Path.Combine(Path.GetTempPath(), "CodexBackupEncrypt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var zipPath = Path.Combine(tempRoot, "package.zip");
        try
        {
            await CreateZipAsync(source, zipPath, cancellationToken);
            var salt = RandomNumberGenerator.GetBytes(SaltSize);
            var header = BuildHeader(salt);
            var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
            try
            {
                await using var input = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 64, FileOptions.SequentialScan | FileOptions.Asynchronous);
                await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 64, FileOptions.SequentialScan | FileOptions.Asynchronous);
                await output.WriteAsync(header, cancellationToken);
                var plain = new byte[ChunkSize];
                var counter = 0UL;
                long processed = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = await input.ReadAsync(plain.AsMemory(), cancellationToken);
                    if (read == 0) break;
                    var nonce = RandomNumberGenerator.GetBytes(NonceSize);
                    var cipher = new byte[read];
                    var tag = new byte[TagSize];
                    var aad = BuildAad(header, counter++);
                    using (var aes = new AesGcm(key, TagSize)) aes.Encrypt(nonce, plain.AsSpan(0, read), cipher, tag, aad);
                    await WriteInt32Async(output, read, cancellationToken);
                    await output.WriteAsync(nonce, cancellationToken);
                    await output.WriteAsync(cipher, cancellationToken);
                    await output.WriteAsync(tag, cancellationToken);
                    processed += read;
                    progress?.Report(new("加密备份", $"已封装 {processed:N0} 字节", checked((long)counter), processed));
                }
                await WriteInt32Async(output, 0, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }
            finally { CryptographicOperations.ZeroMemory(key); }
            return destination;
        }
        catch (IOException ex) { throw new BackupException($"加密备份写入失败：{ex.Message}"); }
        finally
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true); } catch { }
        }
    }

    public static async Task<string> ExtractAsync(string encryptedFile, string password, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ValidatePassword(password);
        var source = PathSafety.Full(encryptedFile);
        if (!File.Exists(source)) throw new BackupException("加密备份文件不存在。");
        var tempRoot = Path.Combine(Path.GetTempPath(), "CodexBackupEncrypted-" + Guid.NewGuid().ToString("N"));
        var packageRoot = Path.Combine(tempRoot, "package");
        Directory.CreateDirectory(packageRoot);
        File.WriteAllText(Path.Combine(tempRoot, ".codex-encrypted-temp"), "1", Encoding.ASCII);
        var zipPath = Path.Combine(tempRoot, "package.zip");
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 64, FileOptions.SequentialScan | FileOptions.Asynchronous))
            await using (var output = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 64, FileOptions.SequentialScan | FileOptions.Asynchronous))
            {
                var header = new byte[HeaderSize];
                await ReadExactlyAsync(input, header, cancellationToken);
                ValidateHeader(header, out var salt);
                var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
                try
                {
                    var counter = 0UL;
                    long processed = 0;
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var length = await ReadInt32Async(input, cancellationToken);
                        if (length == 0) break;
                        if (length < 0 || length > ChunkSize) throw new BackupException("加密备份分块长度无效。");
                        var nonce = new byte[NonceSize]; await ReadExactlyAsync(input, nonce, cancellationToken);
                        var cipher = new byte[length]; await ReadExactlyAsync(input, cipher, cancellationToken);
                        var tag = new byte[TagSize]; await ReadExactlyAsync(input, tag, cancellationToken);
                        var plain = new byte[length];
                        try
                        {
                            using var aes = new AesGcm(key, TagSize);
                            aes.Decrypt(nonce, cipher, tag, plain, BuildAad(header, counter++));
                        }
                        catch (CryptographicException) { throw new BackupException("密码错误，或加密备份已被篡改。"); }
                        await output.WriteAsync(plain, cancellationToken);
                        processed += length;
                        progress?.Report(new("解密备份", $"已解出 {processed:N0} 字节", checked((long)counter), processed));
                    }
                }
                finally { CryptographicOperations.ZeroMemory(key); }
            }
            await ExtractZipAsync(zipPath, packageRoot, cancellationToken);
            if (!File.Exists(Path.Combine(packageRoot, "COMPLETE.json"))) throw new BackupException("解密结果缺少完整标记，不能使用。");
            return packageRoot;
        }
        catch (BackupException) { CleanupExtractedPackage(packageRoot); throw; }
        catch (EndOfStreamException) { CleanupExtractedPackage(packageRoot); throw new BackupException("加密备份不完整或已损坏。"); }
        catch (CryptographicException) { CleanupExtractedPackage(packageRoot); throw new BackupException("密码错误，或加密备份已被篡改。"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { CleanupExtractedPackage(packageRoot); throw new BackupException($"加密备份无法读取：{ex.Message}"); }
    }

    public static void CleanupExtractedPackage(string packagePath)
    {
        try
        {
            var package = PathSafety.Full(packagePath);
            var parent = Directory.GetParent(package);
            if (parent is null || !File.Exists(Path.Combine(parent.FullName, ".codex-encrypted-temp"))) return;
            var tempRoot = parent.FullName;
            if (Path.GetFileName(package).Equals("package", StringComparison.OrdinalIgnoreCase)) Directory.Delete(tempRoot, true);
        }
        catch { }
    }

    private static async Task CreateZipAsync(string source, string zipPath, CancellationToken ct)
    {
        await using var stream = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 64, FileOptions.SequentialScan | FileOptions.Asynchronous);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false);
        var entries = Directory.EnumerateFileSystemEntries(source, "*", SearchOption.AllDirectories).OrderBy(x => x.Length).ThenBy(x => x, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in entries)
        {
            ct.ThrowIfCancellationRequested();
            PathSafety.RejectReparseAncestors(path);
            var relative = Path.GetRelativePath(source, path).Replace('\\', '/');
            if (!seen.Add(relative)) throw new BackupException("加密包来源存在大小写冲突路径。");
            if (Directory.Exists(path)) { archive.CreateEntry(relative.TrimEnd('/') + "/"); continue; }
            if (!File.Exists(path)) throw new BackupException("加密包来源在读取期间发生变化。");
            var entry = archive.CreateEntry(relative, CompressionLevel.Fastest);
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 64, options: FileOptions.SequentialScan | FileOptions.Asynchronous);
            await using var output = entry.Open();
            await input.CopyToAsync(output, 1024 * 64, ct);
        }
    }

    private static async Task ExtractZipAsync(string zipPath, string packageRoot, CancellationToken ct)
    {
        await using var stream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 64, FileOptions.SequentialScan | FileOptions.Asynchronous);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var relative = entry.FullName.Replace('/', '\\').TrimEnd('\\');
            if (string.IsNullOrWhiteSpace(relative)) continue;
            PathSafety.ValidateRelative(relative);
            if (!seen.Add(relative)) throw new BackupException("加密包含有重复路径。");
            var destination = PathSafety.Under(packageRoot, relative);
            if (entry.FullName.EndsWith("/", StringComparison.Ordinal) || entry.FullName.EndsWith("\\", StringComparison.Ordinal)) { Directory.CreateDirectory(destination); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = entry.Open();
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 64, FileOptions.SequentialScan | FileOptions.Asynchronous);
            await input.CopyToAsync(output, 1024 * 64, ct);
        }
    }

    private static byte[] BuildHeader(byte[] salt)
    {
        using var stream = new MemoryStream(HeaderSize);
        stream.Write(Magic); stream.WriteByte(Version);
        stream.Write(BitConverter.GetBytes(Iterations)); stream.Write(BitConverter.GetBytes(ChunkSize)); stream.Write(salt);
        return stream.ToArray();
    }
    private static void ValidateHeader(byte[] header, out byte[] salt)
    {
        if (!header.AsSpan(0, Magic.Length).SequenceEqual(Magic) || header[8] != Version) throw new BackupException("不支持的加密备份格式。");
        var iterations = BitConverter.ToInt32(header, 9); var chunk = BitConverter.ToInt32(header, 13);
        if (iterations != Iterations || chunk != ChunkSize) throw new BackupException("加密备份参数不受支持。");
        salt = header.AsSpan(17, SaltSize).ToArray();
    }
    private static byte[] BuildAad(byte[] header, ulong counter)
    {
        var aad = new byte[header.Length + sizeof(ulong)]; Buffer.BlockCopy(header, 0, aad, 0, header.Length); Buffer.BlockCopy(BitConverter.GetBytes(counter), 0, aad, header.Length, sizeof(ulong)); return aad;
    }
    private static async Task WriteInt32Async(Stream stream, int value, CancellationToken ct) => await stream.WriteAsync(BitConverter.GetBytes(value), ct);
    private static async Task<int> ReadInt32Async(Stream stream, CancellationToken ct) { var bytes = new byte[4]; await ReadExactlyAsync(stream, bytes, ct); return BitConverter.ToInt32(bytes); }
    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), ct);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
    }
    private static void ValidatePassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 8 || password.Length > 1024) throw new BackupException("加密密码需要 8 至 1024 个字符；密码丢失后无法恢复。");
    }
}
