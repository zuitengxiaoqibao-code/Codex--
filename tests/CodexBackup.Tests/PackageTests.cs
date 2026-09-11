using System.Text;
using CodexBackup.Core;
using Xunit;

namespace CodexBackup.Tests;

public sealed class TestTree : IDisposable
{
    public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CodexBackupTests", Guid.NewGuid().ToString("N"));
    public TestTree() => Directory.CreateDirectory(Root);
    public string Dir(string relative) { var p = Path.Combine(Root, relative); Directory.CreateDirectory(p); return p; }
    public string Write(string relative, string text) { var p = Path.Combine(Root, relative); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, text, new UTF8Encoding(false)); return p; }
    public SourceItem Source(string relative, bool required = true) => new() { Path = Path.Combine(Root, relative), Name = relative, Exists = true, Required = required, Kind = SourceKind.Project, IsDirectory = Directory.Exists(Path.Combine(Root, relative)) };
    public BackupRequest Request(params SourceItem[] items) => new() { Sources = items.ToList(), DestinationDirectory = Dir("backups") };
    public void Dispose() { try { Directory.Delete(Root, true); } catch { /* Test evidence may be locked by a failed test; never delete elsewhere. */ } }
}

public class PackageTests
{
    [Fact] public async Task SourceMutationDuringCopyDoesNotPublishSuccess()
    {
        using var t = new TestTree(); var file = t.Write("s/a", "first");
        var progress = new InlineProgress(p => { if (p.Phase == "备份") File.WriteAllText(file, "later"); });
        await Assert.ThrowsAsync<BackupException>(() => new BackupEngine().BackupAsync(t.Request(t.Source("s")), progress));
        Assert.Empty(Directory.GetFiles(t.Dir("backups"), "COMPLETE.json", SearchOption.AllDirectories));
    }

    [Fact] public async Task CancellationAfterCopyStillDoesNotPublishSuccess()
    {
        using var t = new TestTree(); t.Write("s/a", "a");
        using var c = new CancellationTokenSource();
        var progress = new InlineProgress(p => { if (p.Phase == "备份") c.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new BackupEngine().BackupAsync(t.Request(t.Source("s")), progress, c.Token));
        Assert.Empty(Directory.GetFiles(t.Dir("backups"), "COMPLETE.json", SearchOption.AllDirectories));
    }

    private sealed class InlineProgress(Action<OperationProgress> action) : IProgress<OperationProgress>
    { public void Report(OperationProgress value) => action(value); }

    [Fact] public void CoreChildCannotCauseParentProjectTemporaryNamedDataToDisappear()
    {
        using var t = new TestTree(); t.Write("project/tmp/precious.txt", "data"); t.Write("project/.codex/config.toml", "config");
        var parent = t.Source("project");
        var child = t.Source("project/.codex"); child.Kind = SourceKind.Core;
        // Root normalization is exercised separately to avoid live-process checks on the synthetic Core.
        var roots = BackupEngine.NormalizeRoots([parent, child]);
        var records = BackupEngine.Snapshot(roots, [], CancellationToken.None);
        Assert.Contains(records, f => f.RelativePath == "tmp\\precious.txt");
    }

    [Fact] public void DirectoryAlternateDataStreamsMustNotBeSilentlyLost()
    {
        using var t = new TestTree(); var directory = t.Dir("source");
        File.WriteAllText(directory + ":important", "valuable");
        Assert.Throws<BackupException>(() => FileIO.ValidateSourceType(directory));
    }

    [Fact] public async Task UnicodeAndEmptyDirectoriesHaveVerifiedContent()
    {
        using var t = new TestTree();
        var original = t.Write("source/中文😀.txt", "会话和项目\ncontent");
        t.Dir("source/empty");
        var result = await new BackupEngine().BackupAsync(t.Request(t.Source("source")));
        var verified = await new PackageVerifier().VerifyAsync(result.PackagePath);
        Assert.Single(verified.Files, x => !x.IsDirectory);
        Assert.Contains(verified.Files, f => f.IsDirectory && f.RelativePath == "empty");
        var file = verified.Files.Single(f => !f.IsDirectory);
        Assert.Equal(File.ReadAllBytes(original), File.ReadAllBytes(Path.Combine(result.PackagePath, "payload", file.Id)));
        Assert.True(File.Exists(Path.Combine(result.PackagePath, "COMPLETE.json")));
        Assert.Equal(1, result.Manifest.FileCount);
    }

    [Fact] public async Task MandatorySourceCannotBeDeselected()
    {
        using var t = new TestTree(); t.Write("s/a", "a");
        var s = t.Source("s"); s.Selected = false;
        await Assert.ThrowsAsync<BackupException>(() => new BackupEngine().BackupAsync(t.Request(s)));
        Assert.Empty(Directory.GetFileSystemEntries(t.Dir("backups")));
    }

    [Fact] public async Task MissingDependencyBlocksBackup()
    {
        using var t = new TestTree(); t.Write("s/a", "a");
        var s = t.Source("s"); s.DependencyIds.Add("unavailable");
        await Assert.ThrowsAsync<BackupException>(() => new BackupEngine().BackupAsync(t.Request(s)));
    }

    [Fact] public async Task SourceNestedTargetIsRejected()
    {
        using var t = new TestTree(); t.Write("s/a", "a");
        var r = t.Request(t.Source("s")); r.DestinationDirectory = t.Dir("s/backups");
        await Assert.ThrowsAsync<BackupException>(() => new BackupEngine().BackupAsync(r));
    }

    [Fact] public async Task OverlappingSourcesAreCopiedOnce()
    {
        using var t = new TestTree(); t.Write("s/child/a", "a");
        var r = await new BackupEngine().BackupAsync(t.Request(t.Source("s"), t.Source("s/child")));
        Assert.Single(r.Manifest.Roots); Assert.Equal(1, r.Manifest.FileCount);
        Assert.Equal(2, r.Manifest.Roots[0].SourceIds.Count);
    }

    [Fact] public async Task PayloadCorruptionFailsFullVerification()
    {
        using var t = new TestTree(); t.Write("s/a", "a");
        var r = await new BackupEngine().BackupAsync(t.Request(t.Source("s")));
        var p = await new PackageVerifier().VerifyAsync(r.PackagePath);
        File.WriteAllText(Path.Combine(r.PackagePath, "payload", p.Files.Single(x => !x.IsDirectory).Id), "b");
        await Assert.ThrowsAsync<BackupException>(() => new PackageVerifier().VerifyAsync(r.PackagePath));
    }

    [Fact] public async Task MissingCompletionMarkerIsNotAValidBackup()
    {
        using var t = new TestTree(); t.Write("s/a", "a");
        var r = await new BackupEngine().BackupAsync(t.Request(t.Source("s")));
        File.Delete(Path.Combine(r.PackagePath, "COMPLETE.json"));
        await Assert.ThrowsAsync<BackupException>(() => new PackageVerifier().VerifyAsync(r.PackagePath));
    }

    [Fact] public async Task CancellationDoesNotPublishACompletedPackage()
    {
        using var t = new TestTree(); t.Write("s/a", "a");
        using var c = new CancellationTokenSource(); c.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new BackupEngine().BackupAsync(t.Request(t.Source("s")), cancellationToken:c.Token));
        Assert.Empty(Directory.GetFiles(t.Dir("backups"), "COMPLETE.json", SearchOption.AllDirectories));
    }

    [Fact] public async Task MissingRequiredPathCannotSilentlyDisappear()
    {
        using var t = new TestTree();
        await Assert.ThrowsAsync<BackupException>(() => new BackupEngine().BackupAsync(t.Request(t.Source("missing"))));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("a/../../outside")]
    [InlineData("C:\\outside")]
    [InlineData("a:stream")]
    [InlineData("CON.txt")]
    [InlineData("folder./a")]
    [InlineData("/absolute")]
    public void UnsafeRelativeNamesAreRejected(string path) => Assert.Throws<BackupException>(() => PathSafety.ValidateRelative(path));

    [Fact] public async Task UnselectedOptionalSourceIsRecordedAsExcluded()
    {
        using var t = new TestTree(); t.Write("s/a", "a"); t.Write("other/b", "b");
        var optional = t.Source("other", false); optional.Selected = false;
        var r = await new BackupEngine().BackupAsync(t.Request(t.Source("s"), optional));
        Assert.NotEmpty(r.Manifest.Exclusions); Assert.Equal(1, r.Manifest.FileCount);
    }

    [Fact] public async Task LockedFileFailsWithoutPublishingSuccess()
    {
        using var t = new TestTree(); var file = t.Write("s/a", "a");
        using var locked = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await Assert.ThrowsAnyAsync<Exception>(() => new BackupEngine().BackupAsync(t.Request(t.Source("s"))));
        Assert.Empty(Directory.GetFiles(t.Dir("backups"), "COMPLETE.json", SearchOption.AllDirectories));
    }
}
