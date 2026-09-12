using CodexBackup.Core;
using System.Text;
using Xunit;

namespace CodexBackup.Tests;

public sealed class EncryptedPackageTests
{
    [Fact]
    public async Task EncryptedBackupCanBeVerifiedWithCorrectPassword()
    {
        using var t = new TestTree();
        t.Write("source/a.txt", "private data");
        var result = await new BackupEngine().BackupAsync(new BackupRequest
        {
            Sources = [t.Source("source")],
            DestinationDirectory = t.Dir("backups"),
            EncryptionPassword = "correct horse"
        });

        Assert.True(File.Exists(result.PackagePath));
        Assert.False(Directory.Exists(result.PackagePath));
        Assert.True(File.ReadAllBytes(result.PackagePath).AsSpan().IndexOf(Encoding.UTF8.GetBytes("correct horse")) < 0);
        var verified = await new PackageVerifier().VerifyAsync(result.PackagePath, "correct horse");
        Assert.Equal(result.Manifest.BackupId, verified.Manifest.BackupId);
    }

    [Fact]
    public async Task WrongPasswordAndTamperingAreRejected()
    {
        using var t = new TestTree();
        t.Write("source/a.txt", "private data");
        var result = await new BackupEngine().BackupAsync(new BackupRequest
        {
            Sources = [t.Source("source")],
            DestinationDirectory = t.Dir("backups"),
            EncryptionPassword = "correct horse"
        });

        await Assert.ThrowsAsync<BackupException>(() => new PackageVerifier().VerifyAsync(result.PackagePath, "wrong password"));
        var bytes = await File.ReadAllBytesAsync(result.PackagePath);
        bytes[^1] ^= 0x01;
        await File.WriteAllBytesAsync(result.PackagePath, bytes);
        await Assert.ThrowsAsync<BackupException>(() => new PackageVerifier().VerifyAsync(result.PackagePath, "correct horse"));
    }
}
