using System.Text;
using CodexBackup.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CodexBackup.Tests;

public class DiscoveryTests
{
    [Fact]
    public async Task AbsentOptionalLocationsAreNotSelectedAndProjectsCanBeDeselected()
    {
        using var t = new TestTree(); var profile = t.Dir("profile"); t.Dir("profile/.codex");
        var project = t.Dir("repo");
        t.Write("profile/.codex/.codex-global-state.json", "{\"local-projects\":{\"id\":{\"rootPaths\":[\"" + Json(project) + "\"]}}}");
        var scan = await new DiscoveryService().ScanAsync(profile);
        Assert.DoesNotContain(scan.Items, x => !x.Exists && !x.Required && x.Selected);
        var found = Assert.Single(scan.Items, x => x.Path == project && x.Kind == SourceKind.Project);
        Assert.False(found.Required);
    }

    [Fact]
    public async Task SkillSubentriesAreMandatoryAndThreadRootMappingIsFound()
    {
        using var t = new TestTree(); var profile = t.Dir("profile"); t.Dir("profile/.codex/skills/example"); var project = t.Dir("repo");
        t.Write("profile/.codex/.codex-global-state.json", "{\"thread-workspace-root\":{\"thread-id\":\"" + Json(project) + "\"}}");
        var scan = await new DiscoveryService().ScanAsync(profile);
        Assert.Contains(scan.Items, x => x.Kind == SourceKind.Skill && x.Required);
        Assert.Contains(scan.Items, x => x.Kind == SourceKind.Project && x.Path == project);
    }

    [Fact]
    public async Task ExplicitProfileDiscoversRequiredRootsAndKnownCompanions()
    {
        using var t = new TestTree();
        var profile = t.Dir("profile");
        var codex = t.Dir("profile/.codex");
        var project = t.Dir("projects/demo");
        var keyedProject = t.Dir("projects/from-global-key");
        var memory = t.Dir("vault");
        t.Dir("profile/.codex/skills/example");
        t.Dir("profile/.codex/plugins/sample");
        t.Dir("profile/.cc-connect");
        t.Dir("profile/AppData/Roaming/Codex");
        t.Dir("profile/Documents/Codex/session-project");
        t.Write("profile/.codex/projects.json", $$"""{"projects":[{"path":"{{Json(project)}}"}]}""");
        t.Write("profile/.codex/.codex-global-state.json", "{\"project_labels\":{\"" + Json(keyedProject) + "\":\"Demo\"}}");
        t.Write("profile/.codex/memories/obsidian-vault-path.txt", memory);

        var prior = Environment.GetEnvironmentVariable("CODEX_HOME");
        Environment.SetEnvironmentVariable("CODEX_HOME", t.Dir("must-not-leak"));
        try
        {
            var result = await new DiscoveryService().ScanAsync(profile);

            Assert.Equal(Path.GetFullPath(profile), result.UserProfile);
            Assert.Contains(result.Items, x => x.Kind == SourceKind.Core && x.Required && x.Path == Path.GetFullPath(codex));
            Assert.Contains(result.Items, x => x.Kind == SourceKind.Project && x.Path == Path.GetFullPath(project) && x.DiscoveredBy.Contains("projects.json"));
            Assert.Contains(result.Items, x => x.Kind == SourceKind.Project && x.Path == Path.GetFullPath(keyedProject) && x.DiscoveredBy.Contains("global-state"));
            Assert.Contains(result.Items, x => x.Kind == SourceKind.Memory && x.Path == Path.GetFullPath(memory));
            Assert.Contains(result.Items, x => x.Kind == SourceKind.Skill && x.Name == "example");
            Assert.Contains(result.Items, x => x.Kind == SourceKind.Plugin && x.Name == "sample");
            Assert.Contains(result.Items, x => x.Kind == SourceKind.Tool && x.Path.EndsWith(".cc-connect"));
            Assert.Contains(result.Items, x => x.Kind == SourceKind.Application && x.Path.EndsWith(Path.Combine("Roaming", "Codex")));
            Assert.Contains(result.Items, x => x.Kind == SourceKind.Project && x.Path.EndsWith(Path.Combine("Documents", "Codex")));
            Assert.DoesNotContain(result.Items, x => x.Path.Contains("must-not-leak"));
        }
        finally { Environment.SetEnvironmentVariable("CODEX_HOME", prior); }
    }

    [Fact]
    public async Task MissingMandatoryCodexRootRemainsVisibleAndBlocksCoverage()
    {
        using var t = new TestTree();
        var profile = t.Dir("empty-profile");

        var result = await new DiscoveryService().ScanAsync(profile);

        var core = Assert.Single(result.Items, x => x.Kind == SourceKind.Core);
        Assert.True(core.Required);
        Assert.False(core.Exists);
        Assert.Contains(result.Findings, x => x.Level == FindingLevel.Blocker && x.Path == core.Path);
        Assert.Contains(result.Findings, x => x.Code == "known-location-missing" && x.Level == FindingLevel.Info);
        Assert.Contains(result.Findings, x => x.Code == "codex-version-unknown");
    }

    [Fact]
    public async Task GitWorktreeAddsGitDirectoryAndCommonDirectoryDependencies()
    {
        using var t = new TestTree();
        var profile = t.Dir("profile"); t.Dir("profile/.codex");
        var worktree = t.Dir("worktrees/topic");
        var gitDir = t.Dir("repo/.git/worktrees/topic");
        var common = t.Dir("repo/.git");
        t.Write("worktrees/topic/.git", $"gitdir: {gitDir}");
        t.Write("repo/.git/worktrees/topic/commondir", "../..");
        t.Write("profile/.codex/projects.json", $$"""{"path":"{{Json(worktree)}}"}""");

        var result = await new DiscoveryService().ScanAsync(profile);
        var project = Assert.Single(result.Items, x => x.Kind == SourceKind.Project && x.Path == Path.GetFullPath(worktree));
        var dependencies = result.Items.Where(x => project.DependencyIds.Contains(x.Id)).ToList();

        Assert.Contains(dependencies, x => x.Path == Path.GetFullPath(gitDir));
        Assert.Contains(dependencies, x => x.Path == Path.GetFullPath(common));
    }

    [Fact]
    public async Task SqliteProjectMetadataIsReadOnlyAndWalUncertaintyIsReported()
    {
        using var t = new TestTree();
        var profile = t.Dir("profile"); var codex = t.Dir("profile/.codex");
        var project = t.Dir("projects/sqlite-project");
        var database = Path.Combine(codex, "state.sqlite");
        SQLitePCL.Batteries_V2.Init();
        using (var connection = new SqliteConnection($"Data Source={database}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE workspaces (id INTEGER, root_path TEXT); INSERT INTO workspaces VALUES (1, $path)";
            command.Parameters.AddWithValue("$path", project);
            command.ExecuteNonQuery();
        }
        t.Write("profile/.codex/state.sqlite-wal", "synthetic coverage marker");
        var before = File.GetLastWriteTimeUtc(database);

        var result = await new DiscoveryService().ScanAsync(profile);

        Assert.Contains(result.Items, x => x.Kind == SourceKind.Project && x.Path == Path.GetFullPath(project) && x.DiscoveredBy.Contains("SQLite"));
        Assert.Contains(result.Findings, x => x.Code == "sqlite-wal-coverage-uncertain" && x.Path == Path.GetFullPath(database));
        Assert.Equal(before, File.GetLastWriteTimeUtc(database));
        Assert.False(File.Exists(database + "-shm"));
    }

    [Fact]
    public async Task CodexManagedWorktreeAddsRequiredGitDependenciesWithBoundedNavigation()
    {
        using var t = new TestTree(); var profile = t.Dir("profile");
        var worktree = t.Dir("profile/.codex/worktrees/repo/topic");
        var gitDir = t.Dir("git/repo/.git/worktrees/topic");
        var common = t.Dir("git/repo/.git");
        t.Write("profile/.codex/worktrees/repo/topic/.git", $"gitdir: {gitDir}");
        t.Write("git/repo/.git/worktrees/topic/commondir", "../..");

        var result = await new DiscoveryService().ScanAsync(profile);

        var item = Assert.Single(result.Items, x => x.Kind == SourceKind.Project && x.Path == Path.GetFullPath(worktree));
        Assert.True(item.Required);
        Assert.Contains(result.Items, x => item.DependencyIds.Contains(x.Id) && x.Path == Path.GetFullPath(gitDir) && x.Required);
        Assert.Contains(result.Items, x => item.DependencyIds.Contains(x.Id) && x.Path == Path.GetFullPath(common) && x.Required);
    }

    [Fact]
    public async Task ProjectActivityComesFromJsonAndSqliteMetadata()
    {
        using var t = new TestTree(); var profile = t.Dir("profile"); var codex = t.Dir("profile/.codex");
        var jsonProject = t.Dir("projects/json"); var sqliteProject = t.Dir("projects/sqlite");
        t.Write("profile/.codex/.codex-global-state.json", "{\"projects\":[{\"rootPaths\":[\"" + Json(jsonProject) + "\"],\"updatedAt\":\"2026-08-09T10:11:12Z\"}]}");
        var database = Path.Combine(codex, "state.sqlite"); SQLitePCL.Batteries_V2.Init();
        using (var connection = new SqliteConnection($"Data Source={database}"))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE threads (cwd TEXT, updated_at INTEGER); INSERT INTO threads VALUES ($cwd, 1750000000000)";
            command.Parameters.AddWithValue("$cwd", sqliteProject); command.ExecuteNonQuery();
        }

        var result = await new DiscoveryService().ScanAsync(profile);

        Assert.Equal(DateTimeOffset.Parse("2026-08-09T10:11:12Z"), Assert.Single(result.Items, x => x.Path == Path.GetFullPath(jsonProject)).LastActivityUtc);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1750000000000), Assert.Single(result.Items, x => x.Path == Path.GetFullPath(sqliteProject)).LastActivityUtc);
        Assert.Contains("元数据", Assert.Single(result.Items, x => x.Path == Path.GetFullPath(sqliteProject)).Notes);
    }

    [Fact]
    public async Task MsixManifestContributesInstallationAndStaticVersion()
    {
        using var t = new TestTree(); var profile = t.Dir("profile"); t.Dir("profile/.codex");
        var package = t.Dir("profile/AppData/Local/Packages/OpenAI.Codex_test");
        t.Write("profile/AppData/Local/Packages/OpenAI.Codex_test/AppxManifest.xml", "<Package xmlns=\"http://schemas.microsoft.com/appx/manifest/foundation/windows10\"><Identity Name=\"OpenAI.Codex\" Version=\"4.3.2.1\" /></Package>");

        var result = await new DiscoveryService().ScanAsync(profile);

        Assert.Contains(Path.GetFullPath(package), result.InstallationPaths);
        Assert.Equal("4.3.2.1", result.CodexVersion);
    }

    [Fact]
    public void VolumeDescriptionIsChineseAndDiskProbeDoesNotInventValues()
    {
        using var t = new TestTree();
        Assert.Contains("卷", WindowsEnvironment.DescribeVolume(t.Root));
        Assert.All(WindowsEnvironment.GetDiskNumbers(t.Root), number => Assert.True(number >= 0));
    }

    [Fact]
    public async Task ConcurrentScansOnOneServiceKeepProfilesIsolated()
    {
        using var t = new TestTree(); var first = t.Dir("first"); var second = t.Dir("second");
        t.Dir("first/.codex"); t.Dir("second/.codex"); t.Dir("first/.cc-connect"); t.Dir("second/.cc-switch");
        var service = new DiscoveryService();

        var scans = await Task.WhenAll(
            Task.Run(() => service.ScanAsync(first)),
            Task.Run(() => service.ScanAsync(second)));

        Assert.All(scans[0].Items, item => Assert.DoesNotContain(Path.GetFullPath(second), item.Path));
        Assert.All(scans[1].Items, item => Assert.DoesNotContain(Path.GetFullPath(first), item.Path));
    }

    [Fact]
    public async Task ExtendedDriveProjectPathIsNormalizedAndPathListsAreIgnored()
    {
        using var t = new TestTree(); var profile = t.Dir("profile"); t.Dir("profile/.codex");
        var project = t.Dir("repo"); var extended = @"\\?\" + Path.GetFullPath(project);
        t.Write("profile/.codex/projects.json", "{\"rootPaths\":[\"" + Json(extended) + "\"]}");
        t.Write("profile/.codex/config.toml", "NODE_PATH = \"C:\\\\one;C:\\\\two\"");

        var result = await new DiscoveryService().ScanAsync(profile);

        Assert.Contains(result.Items, x => x.Kind == SourceKind.Project && x.Path == Path.GetFullPath(project));
        Assert.DoesNotContain(result.Items, x => x.Kind == SourceKind.Project && x.Path.Contains(';'));
    }

    private static string Json(string value) => value.Replace("\\", "\\\\");
}
