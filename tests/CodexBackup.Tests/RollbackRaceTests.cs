using CodexBackup.Core;
using Xunit;

namespace CodexBackup.Tests;

public class RollbackRaceTests
{
    [Fact]
    public async Task EditsAfterRollbackPreflightAreNotMovedAway()
    {
        using var t = new TestTree();
        t.Write("source/new.txt", "backup");
        t.Write("target/old.txt", "old");
        var backup = await new BackupEngine().BackupAsync(t.Request(t.Source("source")));
        var target = Path.Combine(t.Root, "target");
        var engine = new RestoreEngine { Checkpoint = phase => { if (phase == "RollbackPrepared") File.WriteAllText(Path.Combine(target, "new.txt"), "new work"); } };
        var result = await engine.RestoreAsync(new RestoreRequest { PackagePath = backup.PackagePath, ReplaceExisting = true, Mappings = [new() { RootId = backup.Manifest.Roots.Single().Id, TargetPath = target }] });
        await Assert.ThrowsAsync<BackupException>(() => engine.RollbackAsync(result.JournalPath));
        Assert.Equal("new work", File.ReadAllText(Path.Combine(target, "new.txt")));
    }
}
