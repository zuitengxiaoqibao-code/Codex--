using CodexBackup.Core;
using Xunit;

namespace CodexBackup.Tests;

public class RestoreTests
{
    [Theory]
    [InlineData("Prepared")]
    [InlineData("BeforeOriginalMove")]
    [InlineData("AfterOriginalMove")]
    [InlineData("AfterTargetInstall")]
    public async Task InterruptedTransitionCanRollbackOriginalContent(string phase)
    {
        using var t = new TestTree(); t.Write("s/a", "backup"); t.Write("target/a", "original");
        var b = await new BackupEngine().BackupAsync(t.Request(t.Source("s")));
        var engine = new RestoreEngine { Checkpoint = p => { if (p == phase) throw new IOException("injected interrupted transition"); } };
        var ex = await Assert.ThrowsAsync<BackupException>(() => engine.RestoreAsync(Request(b, Path.Combine(t.Root, "target"), true)));
        var journal = Directory.GetFiles(t.Root, "*.journal.json").Single();
        Assert.Contains(journal, ex.Message);
        await new RestoreEngine().RollbackAsync(journal);
        Assert.Equal("original", File.ReadAllText(Path.Combine(t.Root, "target/a")));
    }

    [Fact] public async Task EditedJournalCannotAuthorizeRollback()
    {
        using var t = new TestTree(); t.Write("s/a", "old"); t.Write("target/a", "new");
        var b = await new BackupEngine().BackupAsync(t.Request(t.Source("s")));
        var result = await new RestoreEngine().RestoreAsync(Request(b, Path.Combine(t.Root, "target"), true));
        var envelope = FileIO.ReadJson<JournalEnvelope>(result.JournalPath);
        envelope.Payload += " ";
        FileIO.WriteJsonDurable(result.JournalPath, envelope);
        await Assert.ThrowsAsync<BackupException>(() => new RestoreEngine().RollbackAsync(result.JournalPath));
        Assert.Equal("old", File.ReadAllText(Path.Combine(t.Root, "target/a")));
    }

    [Fact] public void JournalWriterEnforcesReadableSizeLimit()
    {
        var envelope = JournalProtection.Seal(new RestoreJournal());
        Assert.Throws<BackupException>(() => JournalProtection.ValidateSize(envelope, 10));
    }
    [Fact] public async Task DeclaredProjectKindCannotBypassLiveCodexIsolation()
    {
        using var t = new TestTree(); t.Write("s/a", "a");
        var b = await new BackupEngine().BackupAsync(t.Request(t.Source("s")));
        var target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "never-write-this-test");
        Assert.False((await new RestoreEngine().PreviewAsync(Request(b, target, true))).CanProceed);
        Assert.False(Directory.Exists(target));
    }

    [Fact] public async Task VerifiedRollbackCopyCanRecoverWhenRetiredOriginalIsDamaged()
    {
        using var t = new TestTree(); t.Write("s/a", "old"); t.Write("target/a", "new");
        var b = await new BackupEngine().BackupAsync(t.Request(t.Source("s")));
        var result = await new RestoreEngine().RestoreAsync(Request(b, Path.Combine(t.Root, "target"), true));
        var retired = Directory.GetDirectories(t.Root, "*.original").Single();
        File.WriteAllText(Path.Combine(retired, "a"), "damage");
        await new RestoreEngine().RollbackAsync(result.JournalPath);
        Assert.Equal("new", File.ReadAllText(Path.Combine(t.Root, "target/a")));
    }
    private static RestoreRequest Request(BackupResult backup, string target, bool replace = false) => new()
    { PackagePath = backup.PackagePath, Mappings = [new() { RootId = backup.Manifest.Roots.Single().Id, TargetPath = target }], ReplaceExisting = replace };

    [Fact] public async Task IsolatedRestorePreservesUnicodeAndEmptyFolders()
    {
        using var t = new TestTree(); t.Write("s/数据.txt", "记忆"); t.Dir("s/empty");
        var b = await new BackupEngine().BackupAsync(t.Request(t.Source("s")));
        var request = Request(b, Path.Combine(t.Root, "restored"));
        var preview = await new RestoreEngine().PreviewAsync(request);
        Assert.True(preview.CanProceed);
        var result = await new RestoreEngine().RestoreAsync(request);
        Assert.Equal("记忆", File.ReadAllText(Path.Combine(t.Root, "restored/数据.txt")));
        Assert.True(Directory.Exists(Path.Combine(t.Root, "restored/empty")));
        Assert.Equal("记忆", File.ReadAllText(Path.Combine(t.Root, "s/数据.txt")));
        Assert.True(File.Exists(result.JournalPath));
    }

    [Fact] public async Task ExistingTargetBlocksUnlessExplicitReplacement()
    {
        using var t = new TestTree(); t.Write("s/a", "old"); t.Write("target/a", "new");
        var b = await new BackupEngine().BackupAsync(t.Request(t.Source("s")));
        var request = Request(b, Path.Combine(t.Root, "target"));
        Assert.False((await new RestoreEngine().PreviewAsync(request)).CanProceed);
        await Assert.ThrowsAsync<BackupException>(() => new RestoreEngine().RestoreAsync(request));
        Assert.Equal("new", File.ReadAllText(Path.Combine(t.Root, "target/a")));
    }

    [Fact] public async Task ReplacementRetainsRollbackAndCanUndo()
    {
        using var t = new TestTree(); t.Write("s/a", "old"); t.Write("target/a", "new"); t.Write("target/extra", "keep");
        var b = await new BackupEngine().BackupAsync(t.Request(t.Source("s")));
        var result = await new RestoreEngine().RestoreAsync(Request(b, Path.Combine(t.Root, "target"), true));
        Assert.Equal("old", File.ReadAllText(Path.Combine(t.Root, "target/a")));
        Assert.Single(result.RollbackPaths);
        Assert.Equal("new", File.ReadAllText(Path.Combine(result.RollbackPaths[0], "a")));
        await new RestoreEngine().RollbackAsync(result.JournalPath);
        Assert.Equal("new", File.ReadAllText(Path.Combine(t.Root, "target/a")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(t.Root, "target/extra")));
    }

    [Fact] public async Task TargetCannotBeBackupOrItsParent()
    {
        using var t = new TestTree(); t.Write("s/a", "a");
        var b = await new BackupEngine().BackupAsync(t.Request(t.Source("s")));
        Assert.False((await new RestoreEngine().PreviewAsync(Request(b, t.Root, true))).CanProceed);
        Assert.False((await new RestoreEngine().PreviewAsync(Request(b, Path.Combine(b.PackagePath, "bad"), true))).CanProceed);
    }

    [Fact] public async Task CorruptPackageNeverChangesExistingTarget()
    {
        using var t = new TestTree(); t.Write("s/a", "old"); t.Write("target/a", "new");
        var b = await new BackupEngine().BackupAsync(t.Request(t.Source("s")));
        File.WriteAllText(Path.Combine(b.PackagePath, "manifest.json"), "{}");
        await Assert.ThrowsAsync<BackupException>(() => new RestoreEngine().RestoreAsync(Request(b, Path.Combine(t.Root, "target"), true)));
        Assert.Equal("new", File.ReadAllText(Path.Combine(t.Root, "target/a")));
    }

    [Fact] public async Task RootFileRestoresWithoutInventingDirectory()
    {
        using var t = new TestTree(); t.Write("config.txt", "config");
        var b = await new BackupEngine().BackupAsync(t.Request(t.Source("config.txt")));
        await new RestoreEngine().RestoreAsync(Request(b, Path.Combine(t.Root, "restored.txt")));
        Assert.Equal("config", File.ReadAllText(Path.Combine(t.Root, "restored.txt")));
    }

    [Fact] public async Task CancelledRestoreKeepsExistingData()
    {
        using var t = new TestTree(); t.Write("s/a", "old"); t.Write("target/a", "new");
        var b = await new BackupEngine().BackupAsync(t.Request(t.Source("s")));
        using var ct = new CancellationTokenSource(); ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new RestoreEngine().RestoreAsync(Request(b, Path.Combine(t.Root, "target"), true), cancellationToken:ct.Token));
        Assert.Equal("new", File.ReadAllText(Path.Combine(t.Root, "target/a")));
    }

    [Fact] public async Task RollbackRefusesToOverwriteFilesChangedAfterRestore()
    {
        using var t = new TestTree(); t.Write("s/a", "old"); t.Write("target/a", "new");
        var b = await new BackupEngine().BackupAsync(t.Request(t.Source("s")));
        var result = await new RestoreEngine().RestoreAsync(Request(b, Path.Combine(t.Root, "target"), true));
        File.WriteAllText(Path.Combine(t.Root, "target/a"), "later work");
        await Assert.ThrowsAsync<BackupException>(() => new RestoreEngine().RollbackAsync(result.JournalPath));
        Assert.Equal("later work", File.ReadAllText(Path.Combine(t.Root, "target/a")));
    }
}
