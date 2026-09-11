using CodexBackup.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CodexBackup.Tests;

public class SecurityTests
{
    [Fact] public async Task RehashedHostileInventoryCannotEscapeTarget()
    {
        using var t = new TestTree(); t.Write("s/a", "a");
        var b = await new BackupEngine().BackupAsync(t.Request(t.Source("s")));
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(b.PackagePath, "inventory.sqlite"), Pooling = false }.ToString()))
        {
            db.Open(); using var command = db.CreateCommand();
            command.CommandText = "UPDATE files SET relative_path='../outside' WHERE is_directory=0"; command.ExecuteNonQuery();
        }
        var markerPath = Path.Combine(b.PackagePath, "COMPLETE.json");
        var marker = FileIO.ReadJson<CompletionMarker>(markerPath);
        marker.InventorySha256 = await FileIO.HashAsync(Path.Combine(b.PackagePath, "inventory.sqlite"), CancellationToken.None);
        FileIO.WriteJsonDurable(markerPath, marker);
        await Assert.ThrowsAsync<BackupException>(() => new PackageVerifier().VerifyAsync(b.PackagePath));
    }

    [Fact] public void ParentLeasePreventsDirectoryReplacement()
    {
        using var t = new TestTree(); var parent = t.Dir("parent/child");
        using var lease = DirectoryLease.Acquire(parent);
        Assert.ThrowsAny<IOException>(() => Directory.Move(Path.Combine(t.Root, "parent"), Path.Combine(t.Root, "moved")));
        Assert.True(Directory.Exists(parent));
    }

    [Fact] public async Task BackupAndVerificationLeaveSourceBytesAndTimesUnchanged()
    {
        using var t = new TestTree(); var f = t.Write("source/a.txt", "sensitive fixture");
        var before = File.GetLastWriteTimeUtc(f); var hash = await FileIO.HashAsync(f, CancellationToken.None);
        var b = await new BackupEngine().BackupAsync(t.Request(t.Source("source")));
        await new PackageVerifier().VerifyAsync(b.PackagePath);
        Assert.Equal(hash, await FileIO.HashAsync(f, CancellationToken.None)); Assert.Equal(before, File.GetLastWriteTimeUtc(f));
    }

    [Fact] public async Task LongChinesePathRoundTrips()
    {
        using var t = new TestTree();
        var relative = "s/" + string.Join("/", Enumerable.Repeat(new string('中', 40), 7)) + "/file.txt";
        t.Write(relative, "long path");
        var b = await new BackupEngine().BackupAsync(t.Request(t.Source("s")));
        var target = Path.Combine(t.Root, "restored");
        await new RestoreEngine().RestoreAsync(new() { PackagePath = b.PackagePath, Mappings = [new() { RootId = b.Manifest.Roots[0].Id, TargetPath = target }] });
        Assert.Equal("long path", File.ReadAllText(Path.Combine(target, relative[2..])));
    }
}
