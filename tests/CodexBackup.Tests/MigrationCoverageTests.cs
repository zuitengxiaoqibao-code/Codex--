using System.Diagnostics;
using System.ComponentModel;
using CodexBackup.Core;
using Xunit;

namespace CodexBackup.Tests;

public class MigrationCoverageTests
{
    [Fact]
    public void SourceSelectionPublishesOnlyBindingNotificationsWithoutACollectionRefresh()
    {
        var source = new SourceItem { Exists = true, Selected = true, Required = false };
        var observable = Assert.IsAssignableFrom<INotifyPropertyChanged>(source);
        var changed = new List<string>();
        observable.PropertyChanged += (_, args) => changed.Add(args.PropertyName ?? "");

        source.Selected = false;

        Assert.Contains(nameof(SourceItem.Selected), changed);
        Assert.Contains(nameof(SourceItem.StatusText), changed);
        Assert.Contains(nameof(SourceItem.HasProblem), changed);
        var count = changed.Count;
        source.Selected = false;
        Assert.Equal(count, changed.Count);
    }

    [Fact]
    public void SelectionCoordinatorKeepsSharedProjectUntilLastSessionIsCleared()
    {
        using var t = new TestTree();
        t.Write("core/a.jsonl", "a");
        t.Write("core/b.jsonl", "b");
        t.Write("project/src.cs", "source");
        var core = t.Source("core"); core.Kind = SourceKind.Core; core.Required = true;
        var transcriptA = t.Source("core/a.jsonl"); transcriptA.Kind = SourceKind.Session;
        var transcriptB = t.Source("core/b.jsonl"); transcriptB.Kind = SourceKind.Session;
        var project = t.Source("project"); project.Kind = SourceKind.Project; project.Required = false;
        var groups = new[]
        {
            new SessionSelectionGroup("a", [new() { Id = "a", ProjectPath = project.Path, TranscriptPath = transcriptA.Path }]),
            new SessionSelectionGroup("b", [new() { Id = "b", ProjectPath = project.Path, TranscriptPath = transcriptB.Path }])
        };
        var coordinator = SelectionCoordinator.Build(groups, [core, transcriptA, transcriptB, project], new Dictionary<string, string>());
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "a", "b" };
        coordinator.ApplySessionSelection("a", false, selected);

        Assert.True(project.Selected);
        coordinator.ApplySessionSelection("b", false, selected);
        Assert.False(project.Selected);
    }

    [Fact]
    public void SelectionCoordinatorBatchUsesStableAssociationsWithoutReevaluatingEverySession()
    {
        using var t = new TestTree();
        var core = t.Source("core"); core.Kind = SourceKind.Core; core.Required = true;
        var project = t.Source("project"); project.Kind = SourceKind.Project;
        var groups = Enumerable.Range(0, 1000).Select(i => new SessionSelectionGroup(
            "session-" + i,
            [new() { Id = "session-" + i, ProjectPath = project.Path, TranscriptPath = core.Path, Selected = false }])).ToList();
        var sources = new[] { core, project };
        var coordinator = SelectionCoordinator.Build(groups, sources, new Dictionary<string, string>());
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var started = Stopwatch.StartNew();
        foreach (var group in groups) coordinator.ApplySessionSelection(group.Key, true, selected);
        started.Stop();

        Assert.Equal(1000, selected.Count);
        Assert.True(project.Selected);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(1), $"batch selection took {started.Elapsed}");
    }

    [Fact]
    public void BackupScopeRequiresPersonalDataButNotReinstallableApplicationOrLogs()
    {
        Assert.True(BackupScopePolicy.MustPreserve(new() { Kind = SourceKind.Memory, Name = "memory" }, true));
        Assert.True(BackupScopePolicy.MustPreserve(new() { Kind = SourceKind.Skill, Name = "skill" }, true));
        Assert.True(BackupScopePolicy.MustPreserve(new() { Kind = SourceKind.Environment, Name = "config.toml" }, true));
        Assert.False(BackupScopePolicy.MustPreserve(new() { Kind = SourceKind.Application, Name = "Codex app" }, true));
        Assert.False(BackupScopePolicy.MustPreserve(new() { Kind = SourceKind.Environment, Name = "Codex 日志目录" }, true));
        Assert.False(BackupScopePolicy.SelectByDefault(new() { Kind = SourceKind.Application, Name = "Codex app", Exists = true }, true));
        Assert.False(BackupScopePolicy.SelectByDefault(new() { Kind = SourceKind.Environment, Name = "Codex 日志目录", Exists = true }, true));
    }

    [Fact] public void ScanCountsSeparateUniqueSessionsAssociationsAndProjectLocations()
    {
        var scan = new ScanResult
        {
            Sessions =
            [
                new() { Id = "same", ProjectPath = "C:\\a", TranscriptPath = "C:\\t1" },
                new() { Id = "same", ProjectPath = "C:\\a", TranscriptPath = "C:\\t2" },
                new() { Id = "other", ProjectPath = "D:\\b", TranscriptPath = "D:\\t" }
            ],
            Items =
            [
                new() { Kind = SourceKind.Project, Path = "C:\\a" },
                new() { Kind = SourceKind.Project, Path = "C:\\a" },
                new() { Kind = SourceKind.Project, Path = "D:\\b" }
            ]
        };

        Assert.Equal(2, scan.UniqueSessionCount);
        Assert.Equal(3, scan.SessionAssociationCount);
        Assert.Equal(2, scan.ProjectLocationCount);
    }

    [Fact] public void PreflightBlocksCompleteMigrationWhenTranscriptIsMissing()
    {
        using var t = new TestTree();
        t.Write("home/config.toml", "");
        t.Write("project/app.cs", "source");
        var core = t.Source("home"); core.Kind = SourceKind.Core;
        var project = t.Source("project"); project.Kind = SourceKind.Project;
        var request = new BackupRequest
        {
            CompleteMigration = true,
            Sources = [core, project],
            Sessions = [new() { Id = "a", ProjectPath = project.Path, TranscriptPath = Path.Combine(core.Path, "missing.jsonl") }]
        };
        var scan = new ScanResult { Items = [core, project], Sessions = request.Sessions };

        var report = PreflightReport.Build(scan, request);

        Assert.Equal(PreflightStatus.Blocked, report.Status);
        Assert.False(report.CanReinstall);
        Assert.Contains(report.Findings, f => f.Code == "complete-file-missing");
    }

    [Fact] public async Task BackupManifestPersistsSourceVersionAndProductVersion()
    {
        using var t = new TestTree();
        t.Write("project/app.cs", "source");
        var source = t.Source("project"); source.Kind = SourceKind.Project;
        var result = await new BackupEngine().BackupAsync(new BackupRequest
        {
            Sources = [source],
            DestinationDirectory = t.Dir("backups"),
            SourceCodexVersion = "26.903.9818.0"
        });

        Assert.Equal("26.903.9818.0", result.Manifest.SourceCodexVersion);
        Assert.Equal(ProductInfo.Version, result.Manifest.ToolVersion);
    }

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
    [Fact] public void CompleteModeRejectsIntentionallyOmittedDiscoveredSession()
    {
        using var t = new TestTree(); t.Write("home/config.toml", "");
        var core = t.Source("home"); core.Kind = SourceKind.Core;
        var request = new BackupRequest { CompleteMigration = true, Sources = [core], DiscoveredSessionCount = 2, Sessions = [new() { Id = "one" }] };
        Assert.Contains(MigrationCoverage.Evaluate(request), finding => finding.Code == "complete-session-selection");
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

public sealed class CleanupServiceTests
{
    [Fact]
    public void ArchivedProjectCleanupOnlyListsKnownGeneratedDirectoriesAndLeavesSourceUntouched()
    {
        using var t = new TestTree();
        var project = t.Dir("project");
        t.Write("project/.git/HEAD", "ref: refs/heads/main");
        t.Write("project/src/app.cs", "keep");
        t.Write("project/bin/generated.dll", "generated");
        t.Write("project/obj/project.assets.json", "generated");
        t.Write("project/notes.txt", "keep");
        var session = new SessionReference { Id = "archived", Lifecycle = SessionLifecycle.Archived, ProjectPath = project, TranscriptPath = Path.Combine(t.Root, "transcript.jsonl") };
        var candidates = CleanupService.FindCandidates([session]);

        Assert.Contains(candidates, c => c.CandidatePath.EndsWith(Path.Combine("project", "bin"), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(candidates, c => c.CandidatePath.EndsWith(Path.Combine("project", "obj"), StringComparison.OrdinalIgnoreCase));
        var wholeProject = Assert.Single(candidates, c => c.Kind == CleanupCandidateKind.ArchivedProject);
        Assert.Equal(project, wholeProject.CandidatePath);
        Assert.True(wholeProject.ContainsSource);
        Assert.True(File.Exists(Path.Combine(project, "src", "app.cs")));
        Assert.All(candidates, c => Assert.False(c.Selected));
    }

    [Fact]
    public void UnmarkedArchivedProjectAppearsAsHighRiskCandidate()
    {
        using var t = new TestTree();
        var folder = t.Dir("ordinary-folder");
        t.Write("ordinary-folder/readme.txt", "data");

        var candidates = CleanupService.FindCandidates([
            new SessionReference { Id = "archived", Lifecycle = SessionLifecycle.Archived, ProjectPath = folder }
        ]);

        var candidate = Assert.Single(candidates, item => item.Kind == CleanupCandidateKind.ArchivedProject);
        Assert.True(candidate.ContainsSource);
        Assert.False(candidate.Selected);
    }

    [Fact]
    public void WholeProjectSharedWithActiveSessionIsLocked()
    {
        using var t = new TestTree();
        var project = t.Dir("project");
        t.Write("project/package.json", "{}");
        var candidate = Assert.Single(CleanupService.FindCandidates([
            new SessionReference { Id = "archived", Lifecycle = SessionLifecycle.Archived, ProjectPath = project },
            new SessionReference { Id = "active", Lifecycle = SessionLifecycle.Active, ProjectPath = project }
        ]), item => item.Kind == CleanupCandidateKind.ArchivedProject);

        Assert.True(candidate.ContainsSource);
        Assert.False(candidate.SafeToQuarantine);
    }

    [Fact]
    public void WholeProjectContainingAnActiveSubprojectIsLocked()
    {
        using var t = new TestTree();
        var project = t.Dir("project");
        t.Write("project/package.json", "{}");
        var activeSubproject = t.Dir("project/packages/active");
        t.Write("project/packages/active/package.json", "{}");
        var candidate = Assert.Single(CleanupService.FindCandidates([
            new SessionReference { Id = "archived", Lifecycle = SessionLifecycle.Archived, ProjectPath = project },
            new SessionReference { Id = "active", Lifecycle = SessionLifecycle.Active, ProjectPath = activeSubproject }
        ]), item => item.Kind == CleanupCandidateKind.ArchivedProject && item.ProjectPath == project);

        Assert.False(candidate.SafeToQuarantine);
    }

    [Fact]
    public void CleanupCandidateSharedWithActiveSessionIsNotQuarantinable()
    {
        using var t = new TestTree();
        var project = t.Dir("project");
        t.Write("project/node_modules/package.json", "generated");
        var sessions = new[]
        {
            new SessionReference { Id = "archived", Lifecycle = SessionLifecycle.Archived, ProjectPath = project },
            new SessionReference { Id = "active", Lifecycle = SessionLifecycle.Active, ProjectPath = project }
        };

        var candidate = Assert.Single(CleanupService.FindCandidates(sessions), item => item.Kind == CleanupCandidateKind.GeneratedContent);
        Assert.True(candidate.IsSharedWithActiveSession);
        Assert.False(candidate.SafeToQuarantine);
    }

    [Fact]
    public async Task QuarantineJournalCanRestoreGeneratedDirectory()
    {
        using var t = new TestTree();
        var project = t.Dir("project");
        t.Write("project/bin/generated.dll", "generated");
        var candidate = Assert.Single(CleanupService.FindCandidates([
            new SessionReference { Id = "archived", Lifecycle = SessionLifecycle.Archived, ProjectPath = project }
        ]), item => item.Kind == CleanupCandidateKind.GeneratedContent);
        candidate.Selected = true; candidate.IncludedInVerifiedBackup = true;

        var result = await CleanupService.QuarantineAsync([candidate]);
        Assert.False(Directory.Exists(candidate.CandidatePath));
        var restored = await CleanupService.RestoreAsync(result.JournalPath);

        Assert.Single(restored);
        Assert.True(File.Exists(Path.Combine(project, "bin", "generated.dll")));
    }

    [Fact]
    public async Task CleanupRefusesCandidateThatWasNotCoveredByVerifiedBackup()
    {
        using var t = new TestTree();
        var project = t.Dir("project");
        t.Write("project/bin/generated.dll", "generated");
        var candidate = Assert.Single(CleanupService.FindCandidates([
            new SessionReference { Id = "archived", Lifecycle = SessionLifecycle.Archived, ProjectPath = project }
        ]), item => item.Kind == CleanupCandidateKind.GeneratedContent);
        candidate.Selected = true;

        var result = await CleanupService.QuarantineAsync([candidate]);

        Assert.Empty(result.QuarantinedPaths);
        Assert.True(File.Exists(Path.Combine(project, "bin", "generated.dll")));
    }

    [Fact]
    public async Task WholeArchivedProjectCanBeQuarantinedAndRestored()
    {
        using var t = new TestTree();
        var project = t.Dir("project");
        t.Write("project/package.json", "{}");
        t.Write("project/src/index.js", "source");
        var candidate = Assert.Single(CleanupService.FindCandidates([
            new SessionReference { Id = "archived", Lifecycle = SessionLifecycle.Archived, ProjectPath = project }
        ]), item => item.Kind == CleanupCandidateKind.ArchivedProject);
        candidate.Selected = true; candidate.IncludedInVerifiedBackup = true;

        var result = await CleanupService.QuarantineAsync([candidate]);
        Assert.False(Directory.Exists(project));
        await CleanupService.RestoreAsync(result.JournalPath);

        Assert.True(File.Exists(Path.Combine(project, "src", "index.js")));
    }
}
