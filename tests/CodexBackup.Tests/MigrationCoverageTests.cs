using CodexBackup.Core;
using Xunit;

namespace CodexBackup.Tests;

public class MigrationCoverageTests
{
    [Fact] public void BroadProjectContainingCoreCannotBeCalledComplete()
    {
        using var t=new TestTree(); t.Write("user/.codex/config.toml", "");
        var project=t.Source("user");project.Kind=SourceKind.Project;
        var core=t.Source("user/.codex");core.Kind=SourceKind.Core;
        Assert.Contains(MigrationCoverage.Evaluate(new(){CompleteMigration=true,Sources=[project,core]}),f=>f.Code=="complete-nested-core");
    }
    [Theory]
    [InlineData("session-meta-missing")]
    [InlineData("session-metadata-truncated")]
    public void UnparsedSessionCannotBecomeComplete(string code)
    {
        using var t = new TestTree(); t.Write("home/sessions/a.jsonl", "{}");
        var core = t.Source("home"); core.Kind = SourceKind.Core;
        var request = new BackupRequest { CompleteMigration = true, Sources = [core], DiscoveryFindings = [new(FindingLevel.Warning, code, "metadata unavailable")] };
        Assert.Contains(MigrationCoverage.Evaluate(request), f => f.Code == "complete-discovery-incomplete");
    }
    [Fact] public void CompleteModeRejectsUnselectedSourceEvenWhenSessionWasSelected()
    {
        using var t = new TestTree(); t.Write("home/sessions/a.jsonl", "{}"); t.Write("project/app.cs", "source");
        var core=t.Source("home"); core.Kind=SourceKind.Core;
        var project=t.Source("project"); project.Kind=SourceKind.Project; project.Selected=false;
        var request=new BackupRequest {CompleteMigration=true,Sources=[core,project],Sessions=[new(){Id="a",ProjectPath=project.Path,TranscriptPath=Path.Combine(core.Path,"sessions/a.jsonl")}]};
        Assert.Contains(MigrationCoverage.Evaluate(request),x=>x.Code=="complete-not-selected");
        project.Selected=true; Assert.Empty(MigrationCoverage.Evaluate(request));
    }
    [Fact] public void MovedProjectMustBeExplicitlyLocatedAndIncluded()
    {
        using var t=new TestTree(); t.Write("home/sessions/a.jsonl","{}"); t.Write("moved/app.cs","source");
        var core=t.Source("home");core.Kind=SourceKind.Core; var moved=t.Source("moved"); moved.Kind=SourceKind.Project;
        var old=Path.Combine(t.Root,"missing-project");
        var request=new BackupRequest{CompleteMigration=true,Sources=[core,moved],Sessions=[new(){Id="a",ProjectPath=old,TranscriptPath=Path.Combine(core.Path,"sessions/a.jsonl")}]};
        Assert.Contains(MigrationCoverage.Evaluate(request),x=>x.Code=="complete-file-missing");
        request.PathReplacements[old]=moved.Path; Assert.Empty(MigrationCoverage.Evaluate(request));
    }
    [Fact] public void MissingTranscriptOrIncompleteScanNeverGetsCompleteLabel()
    {
        using var t=new TestTree();t.Write("home/config.toml","");t.Write("project/app.cs","source");
        var core=t.Source("home");core.Kind=SourceKind.Core;var project=t.Source("project");project.Kind=SourceKind.Project;
        var request=new BackupRequest{CompleteMigration=true,Sources=[core,project],Sessions=[new(){Id="a",ProjectPath=project.Path,TranscriptPath=Path.Combine(core.Path,"missing.jsonl")}],DiscoveryFindings=[new(FindingLevel.Warning,"sqlite-wal-coverage-uncertain","technical")]};
        Assert.Contains(MigrationCoverage.Evaluate(request),x=>x.Code=="complete-file-missing");
        Assert.Contains(MigrationCoverage.Evaluate(request),x=>x.Code=="complete-discovery-incomplete");
        request.CompleteMigration=false;Assert.Empty(MigrationCoverage.Evaluate(request));
    }
    [Fact] public void MissingAdviceDescribesImpactAndRecoveryAction()
    {
        var advice=UserGuidance.Explain(new(FindingLevel.Warning,"referenced-project-missing","IOException","D:\\old"));
        Assert.Contains("影响",advice);Assert.Contains("定位已搬走",advice);Assert.DoesNotContain("IOException",advice);
    }
}
