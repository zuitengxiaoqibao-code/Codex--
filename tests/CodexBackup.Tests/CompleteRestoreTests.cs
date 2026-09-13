using CodexBackup.Core;
using Xunit;

namespace CodexBackup.Tests;

public class CompleteRestoreTests
{
    [Fact] public void MultipleCoreHomesRequireExplicitPrimaryForManagedRestore()
    {
        var manifest = new BackupManifest
        {
            Roots =
            [
                new() { Id = "a", Kind = SourceKind.Core, OriginalPath = "C:\\old-core" },
                new() { Id = "b", Kind = SourceKind.Core, OriginalPath = "D:\\other-core" }
            ],
            SourceCodexVersion = "26.903.9818.0"
        };
        var request = new RestoreRequest
        {
            Isolated = false,
            PrimaryCoreRootId = null,
            TargetCodexVersion = "26.903.9818.0",
            Mappings = [new() { RootId = "a", TargetPath = "C:\\new" }, new() { RootId = "b", TargetPath = "D:\\other" }]
        };

        Assert.Throws<BackupException>(() => RestorePlanner.ValidateCoreSelection(manifest, request));
        request.PrimaryCoreRootId = "a";
        RestorePlanner.ValidateCoreSelection(manifest, request);
    }

    [Fact] public void ManagedRestoreBlocksVersionMismatchButIsolationRemainsAvailable()
    {
        var manifest = new BackupManifest
        {
            Roots = [new() { Id = "a", Kind = SourceKind.Core, OriginalPath = "C:\\old-core" }],
            SourceCodexVersion = "26.903.9818.0"
        };
        var request = new RestoreRequest
        {
            Isolated = false,
            TargetCodexVersion = "27.001.0000.0",
            Mappings = [new() { RootId = "a", TargetPath = "C:\\new" }]
        };

        Assert.Throws<BackupException>(() => RestorePlanner.ValidateCoreSelection(manifest, request));
        request.Isolated = true;
        RestorePlanner.ValidateCoreSelection(manifest, request);
    }

    [Fact] public void MultipleCoreHomesNeverShareOneDestination()
    {
        var manifest=new BackupManifest { Roots=[new(){Id="a",Kind=SourceKind.Core,OriginalPath="C:\\old-core"},new(){Id="b",Kind=SourceKind.Core,OriginalPath="D:\\other-core"}] };
        var maps=RestorePlanner.CreateMappings(manifest,"E:\\restored","C:\\new-core",false);
        Assert.Equal(2,maps.Select(m=>m.TargetPath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
    [Fact] public void DisabledIntegrationCountsAsSavedButProjectAndGitMustRemainActive()
    {
        using var t=new TestTree();var stage=t.Dir("stage");t.Dir("stage/.codex-backup-disabled/plugins");
        var source=new SourceItem{Kind=SourceKind.Plugin,Required=true,Path="C:\\old\\plugins",IsDirectory=true};
        var manifest=new BackupManifest{CompleteMigration=true,Roots=[new(){Id="r",OriginalPath="C:\\old",Kind=SourceKind.Core,IsDirectory=true}],LogicalSources=[new(){Kind=SourceKind.Core,Selected=true,Path="C:\\old",IsDirectory=true},source]};
        var package=new VerifiedPackage("D:\\package",manifest,[new(){RootId="r",IsDirectory=true},new(){RootId="r",RelativePath="plugins",IsDirectory=true}]);
        var request=new RestoreRequest{RequireCompleteMigration=true,Isolated=false,Mappings=[new(){RootId="r",TargetPath="E:\\final"}]};
        var physical=new Dictionary<string,string>{{"r",stage}};var notes=new List<string>();
        RestorePlanner.ValidateCoverage(package,request,physical,notes);Assert.Contains(notes,n=>n.Contains("停用"));
        source.Kind=SourceKind.Project;Assert.Throws<BackupException>(()=>RestorePlanner.ValidateCoverage(package,request,physical));
        source.Kind=SourceKind.Environment;source.Path="C:\\old\\.git";t.Dir("stage/.codex-backup-disabled/.git");
        package=package with{Files=[new(){RootId="r",IsDirectory=true},new(){RootId="r",RelativePath=".git",IsDirectory=true}]};
        Assert.Throws<BackupException>(()=>RestorePlanner.ValidateCoverage(package,request,physical));
    }
    [Fact] public async Task WorkspaceAtomPathsRelinkWithoutActivatingOtherAtoms()
    {
        using var t=new TestTree();t.Write("stage/.codex-global-state.json","{\"electron-persisted-atom-state\":{\"other-command\":\"disabled\",\"thread-workspace-state-v1:t\":{\"applied\":{\"cwd\":\"C:/old/p\",\"projectSources\":[\"C:/old/p\"],\"runtimeWorkspaceRoots\":[\"C:/old/p\"]},\"pending\":{\"cwd\":\"C:/old/p\",\"projectSources\":[],\"runtimeWorkspaceRoots\":[]},\"project\":{\"projectId\":\"p\",\"projectKind\":\"local\"},\"revision\":\"1\"}}}");
        await new CoreRestoreAdapter().PrepareAsync(Path.Combine(t.Root,"stage"),"C:/old/core",new Dictionary<string,string>{{"C:/old","D:/new"}});
        var result=File.ReadAllText(Path.Combine(t.Root,"stage/.codex-global-state.json"));Assert.Contains("D:/new/p",result);Assert.DoesNotContain("other-command",result);Assert.Contains("projectId",result);
    }

    [Fact]
    public async Task SupportedConfigPathsRelinkAcrossManagedRestoreLayout()
    {
        using var t = new TestTree();
        var stage = t.Dir("stage");
        var config = t.Write("stage/config.toml", "codex_home = 'C:\\\\old\\\\core'\nmodel_instructions_file = 'C:\\\\old\\\\instructions.md'\n[agents.researcher]\nconfig_file = 'C:\\\\old\\\\agents\\\\researcher.toml'\n[otel.exporter.tls]\nca_certificate = 'C:\\\\old\\\\certs\\\\ca.pem'\n");
        var root = new BackupRoot { Id = "r", OriginalPath = "C:\\old\\core", Kind = SourceKind.Core, IsDirectory = true };
        var package = new VerifiedPackage("E:\\package", new BackupManifest { Roots = [root] }, []);
        var files = new[] { new FileRecord { RootId = root.Id, RelativePath = "config.toml" } };

        await RestorePlanner.PrepareStructuralFilesAsync(stage, root, files, package, new Dictionary<string, string> { ["C:\\old"] = "D:\\new" }, CancellationToken.None);

        var rewritten = File.ReadAllText(config);
        Assert.Contains("codex_home = 'D:\\\\new\\\\core'", rewritten);
        Assert.Contains("model_instructions_file = 'D:\\\\new\\\\instructions.md'", rewritten);
        Assert.Contains("config_file = 'D:\\\\new\\\\agents\\\\researcher.toml'", rewritten);
        Assert.Contains("ca_certificate = 'D:\\\\new\\\\certs\\\\ca.pem'", rewritten);
    }
    [Fact] public void RelocatedLayoutKeepsDriveHierarchyAndCurrentCoreHome()
    {
        var manifest=new BackupManifest{Roots=[new(){Id="a",OriginalPath="D:\\projects\\a",Kind=SourceKind.Project},new(){Id="b",OriginalPath="D:\\projects\\b",Kind=SourceKind.Project},new(){Id="c",OriginalPath="C:\\old-core",Kind=SourceKind.Core}]};
        var maps=RestorePlanner.CreateMappings(manifest,"E:\\restore","C:\\new-core",false);
        Assert.Equal("E:\\restore\\drives\\D\\projects\\a",maps[0].TargetPath);
        Assert.Equal("E:\\restore\\drives\\D\\projects\\b",maps[1].TargetPath);
        Assert.Equal("C:\\new-core",maps[2].TargetPath);
    }
    [Fact] public void CompleteRestoreRejectsLegacyAndMissingTranscript()
    {
        var root=new BackupRoot{Id="r",OriginalPath="C:\\old",IsDirectory=true,Kind=SourceKind.Core};
        var manifest=new BackupManifest{Roots=[root],LogicalSources=[new(){Path=root.OriginalPath,Kind=SourceKind.Core,IsDirectory=true,Selected=true}],Sessions=[new(){ProjectPath="C:\\old\\project",TranscriptPath="C:\\old\\sessions\\a.jsonl"}]};
        var request=new RestoreRequest{Isolated=false,RequireCompleteMigration=true,Mappings=[new(){RootId="r",TargetPath="D:\\new"}]};
        var package=new VerifiedPackage("E:\\package",manifest,[new(){RootId="r",IsDirectory=true},new(){RootId="r",RelativePath="project",IsDirectory=true}]);
        Assert.Throws<BackupException>(()=>RestorePlanner.ValidateCoverage(package,request));
        manifest.CompleteMigration=true;
        Assert.Throws<BackupException>(()=>RestorePlanner.ValidateCoverage(package,request));
        var files=package.Files.ToList();files.Add(new(){RootId="r",RelativePath="sessions\\a.jsonl"});package=package with {Files=files};
        RestorePlanner.ValidateCoverage(package,request);
        request.Mappings.Clear();Assert.Throws<BackupException>(()=>RestorePlanner.ValidateCoverage(package,request));
    }
    [Fact] public void AliasesAndExtendedDrivePathsMapToSameNewSource()
    {
        var manifest=new BackupManifest{Roots=[new(){Id="r",OriginalPath="D:\\found"}],PathReplacements=new(){{"C:\\lost","D:\\found"}}};
        var maps=RestorePlanner.BuildPathMappings(manifest,[new(){RootId="r",TargetPath="E:\\new"}]);
        Assert.Equal("E:\\new\\a",RestorePlanner.Map("\\\\?\\C:\\lost\\a",maps));
    }
    [Fact] public void StagedCoverageChecksActualFilesBeforeSwitch()
    {
        using var t=new TestTree();var stage=t.Dir("stage");t.Dir("stage/project");
        var manifest=new BackupManifest{CompleteMigration=true,Roots=[new(){Id="r",OriginalPath="C:\\old",Kind=SourceKind.Core,IsDirectory=true}],LogicalSources=[new(){Kind=SourceKind.Core,Selected=true,Path="C:\\old",IsDirectory=true}],Sessions=[new(){ProjectPath="C:\\old\\project",TranscriptPath="C:\\old\\session.jsonl"}]};
        var package=new VerifiedPackage("D:\\package",manifest,[new(){RootId="r",IsDirectory=true},new(){RootId="r",RelativePath="project",IsDirectory=true},new(){RootId="r",RelativePath="session.jsonl"}]);
        var request=new RestoreRequest{RequireCompleteMigration=true,Isolated=false,Mappings=[new(){RootId="r",TargetPath="E:\\final"}]};
        Assert.Throws<BackupException>(()=>RestorePlanner.ValidateCoverage(package,request,new Dictionary<string,string>{{"r",stage}}));
        t.Write("stage/session.jsonl","fixture");RestorePlanner.ValidateCoverage(package,request,new Dictionary<string,string>{{"r",stage}});
    }
    [Fact] public async Task SessionMetadataRelinksWithoutChangingDialogueBytes()
    {
        using var t=new TestTree();var file=t.Write("session.jsonl","{\"type\":\"session_meta\",\"payload\":{\"cwd\":\"C:/old/project\",\"id\":\"a\"}}\n{\"type\":\"message\",\"text\":\"C:/old/project must stay\"}\r\n");
        var tail=File.ReadAllBytes(file).SkipWhile(b=>b!=10).Skip(1).ToArray();
        await RestorePlanner.RewriteSessionMetaAsync(file,new Dictionary<string,string>{{"C:\\old","D:\\new"}},CancellationToken.None);
        Assert.Equal(tail,File.ReadAllBytes(file).SkipWhile(b=>b!=10).Skip(1).ToArray());
        Assert.Contains("D:\\\\new\\\\project",File.ReadAllText(file));
    }
    [Fact] public async Task GitWorktreePointersUseFinalMappedPaths()
    {
        using var t=new TestTree();var stage=t.Dir("stage");t.Write("stage/.git","gitdir: C:/old/repo/.git/worktrees/w\n");
        var root=new BackupRoot{Id="r",OriginalPath="C:\\old\\worktree",IsDirectory=true,Kind=SourceKind.Project};
        var package=new VerifiedPackage("E:\\package",new BackupManifest(),[]);
        await RestorePlanner.PrepareStructuralFilesAsync(stage,root,[new(){RelativePath=".git"}],package,new Dictionary<string,string>{{"C:\\old","D:\\new"}},CancellationToken.None);
        Assert.Equal("gitdir: D:\\new\\repo\\.git\\worktrees\\w",File.ReadAllText(Path.Combine(stage,".git")).Trim());
    }
    [Fact] public async Task SeparateGitMetadataRootAndFileRootAreRelinked()
    {
        using var t=new TestTree();var stage=t.Dir("metadata");t.Write("metadata/commondir","../..");t.Write("metadata/gitdir","C:/old/worktree/.git");
        var maps=new Dictionary<string,string>{{"C:\\old","D:\\new"}};
        var package=new VerifiedPackage("E:\\package",new BackupManifest(),[]);
        await RestorePlanner.PrepareStructuralFilesAsync(stage,new(){Id="r",OriginalPath="C:\\old\\repo\\.git\\worktrees\\w",IsDirectory=true},[new(){RelativePath="commondir"},new(){RelativePath="gitdir"}],package,maps,CancellationToken.None);
        Assert.Equal("D:\\new\\repo\\.git",File.ReadAllText(Path.Combine(stage,"commondir")).Trim());
        Assert.Equal("D:\\new\\worktree\\.git",File.ReadAllText(Path.Combine(stage,"gitdir")).Trim());
        var file=t.Write("single-git-file","gitdir:C:/old/repo/.git/worktrees/w");
        await RestorePlanner.PrepareStructuralFilesAsync(file,new(){Id="f",OriginalPath="C:\\old\\worktree\\.git",IsDirectory=false},[new(){RelativePath=""}],package,maps,CancellationToken.None);
        Assert.Equal("gitdir: D:\\new\\repo\\.git\\worktrees\\w",File.ReadAllText(file).Trim());
    }
    [Fact] public async Task NestedCoreWorktreeGitPointerIsRelinked()
    {
        using var t=new TestTree();var stage=t.Dir("core");t.Write("core/worktrees/w/project/.git","gitdir: C:/old/repo/.git/worktrees/w");
        await RestorePlanner.PrepareStructuralFilesAsync(stage,new(){Id="c",OriginalPath="C:\\old\\core",IsDirectory=true,Kind=SourceKind.Core},[new(){RelativePath="worktrees\\w\\project\\.git"}],new VerifiedPackage("E:\\package",new BackupManifest(),[]),new Dictionary<string,string>{{"C:\\old","D:\\new"}},CancellationToken.None);
        Assert.Equal("gitdir: D:\\new\\repo\\.git\\worktrees\\w",File.ReadAllText(Path.Combine(stage,"worktrees/w/project/.git")).Trim());
    }
    [Fact] public async Task ObjectProjectsAndManagedWorktreesRemainConnected()
    {
        using var t=new TestTree();t.Write("stage/.codex-global-state.json","{\"local-projects\":{\"p\":{\"name\":\"P\",\"rootPaths\":[\"C:/old/project\"]}},\"thread-projectless-output-directories\":{\"t\":\"C:/old/output\"}}");t.Write("stage/worktrees/w/source.txt","source");
        await new CoreRestoreAdapter().PrepareAsync(Path.Combine(t.Root,"stage"),"C:/old/core",new Dictionary<string,string>{{"C:/old","D:/new"}});
        Assert.True(File.Exists(Path.Combine(t.Root,"stage/worktrees/w/source.txt")));
        var result=File.ReadAllText(Path.Combine(t.Root,"stage/.codex-global-state.json"));Assert.Contains("D:/new/project",result);Assert.Contains("D:/new/output",result);
    }
}
