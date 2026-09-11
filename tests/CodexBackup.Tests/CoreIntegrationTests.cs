using CodexBackup.Core;
using Xunit;

namespace CodexBackup.Tests;

public class CoreIntegrationTests
{
    // Build a synthetic Core package without asking the backup engine to inspect live Core state.
    // The normal verifier still validates the newly sealed manifest and every payload byte.
    private static async Task<BackupResult> Fixture(TestTree t, bool recognized=true)
    {
        t.Write(recognized?"synthetic-home/sessions/a.jsonl":"synthetic-home/unknown.txt","historical payload");
        t.Write("synthetic-home/config.toml","do not activate");
        var backup=await new BackupEngine().BackupAsync(t.Request(t.Source("synthetic-home")));
        backup.Manifest.Roots.Single().Kind=SourceKind.Core;
        var manifestPath=Path.Combine(backup.PackagePath,"manifest.json");
        FileIO.WriteJsonDurable(manifestPath,backup.Manifest);
        var markerPath=Path.Combine(backup.PackagePath,"COMPLETE.json");
        var marker=FileIO.ReadJson<CompletionMarker>(markerPath);
        marker.ManifestSha256=await FileIO.HashAsync(manifestPath,CancellationToken.None);
        FileIO.WriteJsonDurable(markerPath,marker);
        return backup;
    }
    private static RestoreRequest Request(BackupResult b,string target,bool isolated=false)=>new()
    {PackagePath=b.PackagePath,Isolated=isolated,ReplaceExisting=true,Mappings=[new(){RootId=b.Manifest.Roots.Single().Id,TargetPath=target}]};

    [Fact] public async Task ControlledCorePreviewSelectsAdapterAndUnknownShapeBlocks()
    {
        using var t=new TestTree();var backup=await Fixture(t);
        var preview=await new RestoreEngine().PreviewAsync(Request(backup,Path.Combine(t.Root,"target")));
        Assert.True(preview.CanProceed);Assert.Contains(preview.Findings,f=>f.Code=="CORE_ADAPT");
        using var other=new TestTree();var unknown=await Fixture(other,false);
        var rejected=await new RestoreEngine().PreviewAsync(Request(unknown,Path.Combine(other.Root,"target")));
        Assert.False(rejected.CanProceed);Assert.Contains(rejected.Findings,f=>f.Level==FindingLevel.Blocker);
        Assert.False(Directory.Exists(Path.Combine(other.Root,"target")));
    }

    [Fact] public async Task ControlledCoreRespectsWriterGuardBeforeAnyTargetOrJournalMutation()
    {
        using var t=new TestTree();var backup=await Fixture(t);t.Write("target/original.txt","original");
        var target=Path.Combine(t.Root,"target");var request=Request(backup,target);
        var writers=WriterGuard.RunningWriters();
        if(writers.Count>0)
        {
            var failure=await Assert.ThrowsAsync<BackupException>(()=>new RestoreEngine().RestoreAsync(request));
            Assert.Contains("仍在运行",failure.Message);
            Assert.Equal("original",File.ReadAllText(Path.Combine(target,"original.txt")));
            Assert.Empty(Directory.GetFileSystemEntries(t.Root,".codex-restore-*"));
        }
        else
        {
            // On a runner with no Codex writers, exercise the real adapter/install/rollback path.
            var result=await new RestoreEngine().RestoreAsync(request);
            Assert.True(File.Exists(Path.Combine(target,".codex-backup-disabled/config.toml")));
            Assert.False(File.Exists(Path.Combine(target,"config.toml")));
            Assert.Equal("historical payload",File.ReadAllText(Path.Combine(target,"sessions/a.jsonl")));
            await new RestoreEngine().RollbackAsync(result.JournalPath);
            Assert.Equal("original",File.ReadAllText(Path.Combine(target,"original.txt")));
        }
        await new PackageVerifier().VerifyAsync(backup.PackagePath);
    }

    [Fact] public async Task IsolatedCoreRestoreBypassesAdaptationButNeverActivatesLiveHome()
    {
        using var t=new TestTree();var backup=await Fixture(t);var target=Path.Combine(t.Root,"isolated");
        var result=await new RestoreEngine().RestoreAsync(Request(backup,target,true));
        Assert.True(File.Exists(Path.Combine(target,"config.toml")));
        Assert.False(Directory.Exists(Path.Combine(target,".codex-backup-disabled")));
        Assert.Equal("historical payload",File.ReadAllText(Path.Combine(target,"sessions/a.jsonl")));
        Assert.Contains(result.Notes,n=>n.Contains("隔离"));
        await new RestoreEngine().RollbackAsync(result.JournalPath);
        Assert.False(Directory.Exists(target));
    }
}
