using CodexBackup.Core;
using Xunit;

namespace CodexBackup.Tests;

public sealed class RestoreAcceptanceTests
{
    [Fact]
    public void ValidatesSessionProjectTranscriptGitAndMemoryPairing()
    {
        using var t = new TestTree();
        var core = t.Dir("restored/core");
        var project = t.Dir("restored/project");
        var memory = t.Dir("restored/vault");
        var gitMeta = t.Dir("restored/git-meta");
        t.Write("restored/core/sessions/a.jsonl", "session");
        t.Write("restored/core/memories/obsidian-vault-path.txt", memory + Environment.NewLine);
        t.Write("restored/project/.git", $"gitdir: {gitMeta}{Environment.NewLine}");
        t.Write("restored/project/main.cs", "source");
        var manifest = new BackupManifest
        {
            CompleteMigration = true,
            Roots =
            [
                new() { Id = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", OriginalPath = "C:\\old-core", Kind = SourceKind.Core, IsDirectory = true },
                new() { Id = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", OriginalPath = "D:\\old-project", Kind = SourceKind.Project, IsDirectory = true },
                new() { Id = "cccccccccccccccccccccccccccccccc", OriginalPath = "E:\\old-vault", Kind = SourceKind.Memory, IsDirectory = true }
            ],
            Sessions = [new() { Id = "session-1", ProjectPath = "D:\\old-project", TranscriptPath = "C:\\old-core\\sessions\\a.jsonl" }],
            LogicalSources =
            [
                new() { Kind = SourceKind.Core, Path = "C:\\old-core", IsDirectory = true, Selected = true },
                new() { Kind = SourceKind.Project, Path = "D:\\old-project", IsDirectory = true, Selected = true },
                new() { Kind = SourceKind.Memory, Path = "E:\\old-vault", IsDirectory = true, Selected = true }
            ]
        };
        var files =
            new[]
            {
                new FileRecord { Id = "1", RootId = manifest.Roots[0].Id, IsDirectory = true },
                new FileRecord { Id = "2", RootId = manifest.Roots[0].Id, RelativePath = "sessions", IsDirectory = true },
                new FileRecord { Id = "3", RootId = manifest.Roots[0].Id, RelativePath = "sessions\\a.jsonl" },
                new FileRecord { Id = "4", RootId = manifest.Roots[0].Id, RelativePath = "memories", IsDirectory = true },
                new FileRecord { Id = "5", RootId = manifest.Roots[0].Id, RelativePath = "memories\\obsidian-vault-path.txt" },
                new FileRecord { Id = "6", RootId = manifest.Roots[1].Id, IsDirectory = true },
                new FileRecord { Id = "7", RootId = manifest.Roots[1].Id, RelativePath = ".git" },
                new FileRecord { Id = "8", RootId = manifest.Roots[1].Id, RelativePath = "main.cs" },
                new FileRecord { Id = "9", RootId = manifest.Roots[2].Id, IsDirectory = true }
            };
        var package = new VerifiedPackage(t.Root, manifest, files);
        var request = new RestoreRequest
        {
            RequireCompleteMigration = true,
            Isolated = false,
            PrimaryCoreRootId = manifest.Roots[0].Id,
            Mappings =
            [
                new() { RootId = manifest.Roots[0].Id, TargetPath = core },
                new() { RootId = manifest.Roots[1].Id, TargetPath = project },
                new() { RootId = manifest.Roots[2].Id, TargetPath = memory }
            ]
        };

        var report = RestoreAcceptance.Validate(package, request, new Dictionary<string, string>
        {
            [manifest.Roots[0].Id] = core,
            [manifest.Roots[1].Id] = project,
            [manifest.Roots[2].Id] = memory
        });

        Assert.Equal(RestoreStructuralStatus.Passed, report.StructuralStatus);
        Assert.Equal(RestoreApplicationStatus.PendingManualCheck, report.ApplicationStatus);
        Assert.Contains(report.Checks, check => check.Code == "SESSION_PAIR");
    }

    [Fact]
    public void BlocksMissingTranscriptAndDuplicateProjectTargets()
    {
        var manifest = new BackupManifest
        {
            CompleteMigration = true,
            Roots = [new() { Id = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", OriginalPath = "C:\\old", Kind = SourceKind.Core, IsDirectory = true }],
            Sessions = [new() { Id = "missing", ProjectPath = "C:\\old\\project", TranscriptPath = "C:\\old\\missing.jsonl" }]
        };
        var package = new VerifiedPackage("D:\\package", manifest,
        [new() { Id = "1", RootId = manifest.Roots[0].Id, IsDirectory = true }, new() { Id = "2", RootId = manifest.Roots[0].Id, RelativePath = "project", IsDirectory = true }]);
        var request = new RestoreRequest
        {
            Isolated = false,
            Mappings =
            [
                new() { RootId = manifest.Roots[0].Id, TargetPath = "C:\\same" },
                new() { RootId = manifest.Roots[0].Id, TargetPath = "C:\\same" }
            ]
        };

        var report = RestoreAcceptance.Validate(package, request, new Dictionary<string, string>
        {
            [manifest.Roots[0].Id] = "C:\\same"
        });

        Assert.Equal(RestoreStructuralStatus.Blocked, report.StructuralStatus);
        Assert.Contains(report.Checks, check => check.Code == "TRANSCRIPT_MISSING");
        Assert.Contains(report.Checks, check => check.Code == "DUPLICATE_TARGET");
    }
}
