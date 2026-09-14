using System.Text;
using CodexBackup.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CodexBackup.Tests;

public class DiscoveryTests
{
    [Fact]
    public void EnvironmentInventoryRedactsSecretValuesButKeepsNames()
    {
        var manifest = EnvironmentInventory.CollectFromVariables([
            new KeyValuePair<string, string?>("OPENAI_API_KEY", "sk-live-secret"),
            new KeyValuePair<string, string?>("CODEX_HOME", "D:\\codex")
        ]);

        var serialized = System.Text.Json.JsonSerializer.Serialize(manifest);
        Assert.Contains("OPENAI_API_KEY", serialized);
        Assert.DoesNotContain("sk-live-secret", serialized);
        Assert.DoesNotContain("D:\\codex", serialized);
        Assert.Contains(manifest.Entries, x => x.DisplayName == "OPENAI_API_KEY" && x.Risk == "敏感" && x.Coverage == "仅保存名称");
    }

    [Fact]
    public void EnvironmentInventoryListsProjectLockfilesWithoutRunningScripts()
    {
        using var t = new TestTree();
        var project = t.Dir("project");
        t.Write("project/package.json", "{\"scripts\":{\"preinstall\":\"echo SHOULD_NOT_RUN > marker.txt\"}}");
        t.Write("project/package-lock.json", "{}");
        t.Write("project/requirements.txt", "requests==2.0");
        var item = t.Source("project"); item.Kind = SourceKind.Project;

        var manifest = EnvironmentInventory.Collect(t.Root, [item]);

        Assert.Contains(manifest.Entries, x => x.Category == "锁定文件" && x.SourcePath!.EndsWith("package-lock.json", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(manifest.Entries, x => x.Category == "锁定文件" && x.SourcePath!.EndsWith("requirements.txt", StringComparison.OrdinalIgnoreCase));
        Assert.False(File.Exists(Path.Combine(project, "marker.txt")));
    }

    [Fact]
    public async Task EnvironmentReportDistinguishesBackedUpProfileConfigFromExternalState()
    {
        using var t = new TestTree();
        var profile = t.Dir("profile");
        t.Dir("profile/.codex");
        t.Write("profile/.codex/work.config.toml", "model = 'gpt-test'\n");

        var result = await new DiscoveryService().ScanAsync(profile);

        Assert.Contains(result.EnvironmentManifest.Entries, x => x.Category == "Codex 配置层" && x.DisplayName == "work.config.toml" && x.Coverage == "已纳入备份");
        Assert.Contains(result.EnvironmentManifest.Entries, x => x.DisplayName == "Windows 计划任务" && x.Coverage == "需在新系统重建");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MetadataContractDoesNotConfuseLargeDialogueWithMissingMetadata(bool payload)
    {
        using var t=new TestTree();var profile=t.Dir("profile");var project=t.Dir("repo");
        var fields="\"id\":\"a\",\"cwd\":\""+Json(project)+"\"";
        var first=payload ? "{\"type\":\"session_meta\",\"payload\":{"+fields+"}}" : "{\"type\":\"session_meta\","+fields+"}";
        t.Write("profile/.codex/sessions/a.jsonl",first+"\n"+new string('x',1100000));
        var scan=await new DiscoveryService().ScanAsync(profile);
        Assert.Single(scan.Sessions);
        Assert.DoesNotContain(scan.Findings,f=>f.Code=="session-metadata-truncated");
        Assert.Equal(!payload,scan.Findings.Any(f=>f.Code=="session-metadata-unsupported"));
    }
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
    public void OfficialWindowsSystemConfigPathsUseProgramDataOpenAiCodex()
    {
        var paths = WindowsEnvironment.GetCodexSystemConfigPaths(Path.Combine("C:\\ProgramData"));

        Assert.Equal(Path.Combine("C:\\ProgramData", "OpenAI", "Codex", "config.toml"), paths.ConfigPath);
        Assert.Equal(Path.Combine("C:\\ProgramData", "OpenAI", "Codex", "requirements.toml"), paths.RequirementsPath);
    }

    [Fact]
    public async Task ConfigExternalStateAndLogDirectoriesBecomeRequiredSources()
    {
        using var t = new TestTree();
        var profile = t.Dir("profile");
        var core = t.Dir("profile/.codex");
        var sqliteHome = t.Dir("data/sqlite");
        var logDir = t.Dir("data/logs");
        t.Write("profile/.codex/config.toml", $"sqlite_home = '{sqliteHome.Replace("\\", "\\\\")}'\nlog_dir = '{logDir.Replace("\\", "\\\\")}'\n");

        var result = await new DiscoveryService().ScanAsync(profile);

        Assert.Contains(result.Items, x => x.Path == Path.GetFullPath(sqliteHome) && x.Required && x.Kind == SourceKind.Environment);
        Assert.Contains(result.Items, x => x.Path == Path.GetFullPath(logDir) && x.Required && x.Kind == SourceKind.Environment);
    }

    [Fact]
    public async Task CodexSqliteHomeEnvironmentOverrideIsDiscovered()
    {
        using var t = new TestTree();
        var sqliteHome = t.Dir("external-sqlite");
        var prior = Environment.GetEnvironmentVariable("CODEX_SQLITE_HOME");
        try
        {
            Environment.SetEnvironmentVariable("CODEX_SQLITE_HOME", sqliteHome);
            var result = await new DiscoveryService().ScanAsync(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            Assert.Contains(result.Items, x => x.Kind == SourceKind.Environment && x.Required && x.Path == Path.GetFullPath(sqliteHome));
        }
        finally { Environment.SetEnvironmentVariable("CODEX_SQLITE_HOME", prior); }
    }

    [Fact]
    public async Task RuntimeThreadHistoryDatabaseCreatesSessionReference()
    {
        using var t = new TestTree();
        var profile = t.Dir("profile");
        var core = t.Dir("profile/.codex");
        var project = t.Dir("repo");
        var transcript = t.Write("rollouts/thread.jsonl", "{}\n");
        SQLitePCL.Batteries_V2.Init();
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(core, "thread_history_1.sqlite")}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE threads (id TEXT, title TEXT, cwd TEXT, rollout_path TEXT, updated_at INTEGER); INSERT INTO threads VALUES ('history-1','历史会话',$cwd,$rollout,1750000000)";
            command.Parameters.AddWithValue("$cwd", project);
            command.Parameters.AddWithValue("$rollout", transcript);
            command.ExecuteNonQuery();
        }

        var result = await new DiscoveryService().ScanAsync(profile);

        var session = Assert.Single(result.Sessions, x => x.Id == "history-1");
        Assert.Equal(Path.GetFullPath(transcript), session.TranscriptPath);
        Assert.Equal(Path.GetFullPath(project), session.ProjectPath);
    }

    [Fact]
    public async Task AdditionalSearchRecognizesCoreContainingOnlyOfficialThreadHistoryDatabase()
    {
        using var t = new TestTree();
        var profile = t.Dir("profile");
        var search = t.Dir("other-drive");
        var core = t.Dir("other-drive/custom-codex");
        var project = t.Dir("other-drive/project");
        var transcript = t.Write("other-drive/rollouts/thread.jsonl", "{}\n");
        SQLitePCL.Batteries_V2.Init();
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(core, "thread_history_1.sqlite")}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE threads (id TEXT, cwd TEXT, rollout_path TEXT); INSERT INTO threads VALUES ('nested-history',$cwd,$rollout)";
            command.Parameters.AddWithValue("$cwd", project);
            command.Parameters.AddWithValue("$rollout", transcript);
            command.ExecuteNonQuery();
        }

        var result = await new DiscoveryService().ScanAsync(profile, additionalRoots: [search]);

        Assert.Contains(result.Items, x => x.Kind == SourceKind.Core && x.Path == Path.GetFullPath(core) && x.Required);
        Assert.Contains(result.Sessions, x => x.Id == "nested-history");
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

    [Fact]
    public async Task TomlQuotedProjectAndConfiguredSessionDirectoryProduceLinkedSession()
    {
        using var t = new TestTree(); var profile = t.Dir("profile"); t.Dir("profile/.codex");
        var project = t.Dir("external/project"); var sessions = t.Dir("external/session-store");
        var transcript = t.Write("external/session-store/2026/09/thread.jsonl",
            "{\"type\":\"session_meta\",\"payload\":{\"id\":\"thread-1\",\"title\":\"迁移讨论\",\"cwd\":\"" + Json(project) + "\",\"timestamp\":\"2026-09-10T01:02:03Z\"}}\n{\"type\":\"message\",\"payload\":{\"text\":\"不得暴露\"}}");
        t.Write("profile/.codex/config.toml", "[projects.'" + project.Replace("\\", "\\\\") + "']\ntrust_level = \"trusted\"\nsessions_dir = '" + sessions.Replace("\\", "\\\\") + "'");

        var result = await new DiscoveryService().ScanAsync(profile);

        Assert.Contains(result.Items, x => x.Kind == SourceKind.Project && x.Path == Path.GetFullPath(project) && !x.Required);
        Assert.Contains(result.Items, x => x.Kind == SourceKind.Session && x.Path == Path.GetFullPath(transcript));
        var session = Assert.Single(result.Sessions, x => x.Id == "thread-1");
        Assert.Equal("迁移讨论", session.Title);
        Assert.Equal(Path.GetFullPath(project), session.ProjectPath);
        Assert.Equal(Path.GetFullPath(transcript), session.TranscriptPath);
        Assert.Equal(Path.GetFullPath(Path.Combine(profile, ".codex")), session.CorePath);
    }

    [Fact]
    public async Task SqliteRolloutPathCreatesSessionReferenceAndAdditionalRootIsScanned()
    {
        using var t = new TestTree(); var profile = t.Dir("profile"); var core = t.Dir("profile/.codex");
        var project = t.Dir("elsewhere/source"); var transcript = t.Write("elsewhere/rollouts/a.jsonl", "{}\n");
        var database = Path.Combine(core, "state.sqlite"); SQLitePCL.Batteries_V2.Init();
        using (var connection = new SqliteConnection($"Data Source={database}"))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE threads (id TEXT, title TEXT, cwd TEXT, rollout_path TEXT, updated_at INTEGER); INSERT INTO threads VALUES ('db-1','数据库会话',$cwd,$rollout,1750000000)";
            command.Parameters.AddWithValue("$cwd", project); command.Parameters.AddWithValue("$rollout", transcript); command.ExecuteNonQuery();
        }

        var result = await new DiscoveryService().ScanAsync(profile, additionalRoots: [Path.GetDirectoryName(transcript)!]);

        Assert.Contains(result.Items, x => x.Kind == SourceKind.Session && x.Path == Path.GetFullPath(transcript));
        var session = Assert.Single(result.Sessions, x => x.Id == "db-1");
        Assert.Equal("数据库会话", session.Title);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1750000000), session.LastActivityUtc);
    }

    [Fact]
    public async Task AdditionalSearchParentFindsNestedCoreWithoutRequiringMissingDefaultOrParent()
    {
        using var t = new TestTree(); var profile = t.Dir("profile"); var search = t.Dir("search-drive");
        var nestedCore = t.Dir("search-drive/user-data/.codex"); var project = t.Dir("search-drive/repos/demo");
        t.Dir("search-drive/repos/demo/.git");
        t.Write("search-drive/user-data/.codex/config.toml", "project_path = '" + project + "' # trailing comment");

        var result = await new DiscoveryService().ScanAsync(profile, additionalRoots: [search]);

        Assert.Contains(result.Items, x => x.Kind == SourceKind.Core && x.Path == Path.GetFullPath(nestedCore) && x.Required);
        Assert.Contains(result.Items, x => x.Kind == SourceKind.Project && x.Path == Path.GetFullPath(project) && !x.Required);
        Assert.DoesNotContain(result.Findings, x => x.Code == "required-codex-root-missing" && x.Path == Path.Combine(profile, ".codex"));
        Assert.DoesNotContain(result.Items, x => x.Path == Path.GetFullPath(search) && x.Required);
    }

    [Fact]
    public async Task ProfileConfigAndProjectLocalConfigReferencesAreDiscovered()
    {
        using var t = new TestTree();
        var profile = t.Dir("profile");
        var core = t.Dir("profile/.codex");
        var project = t.Dir("repo/demo");
        var profileInstructions = t.Write("external/profile-instructions.md", "profile");
        var projectInstructions = t.Write("external/project-instructions.md", "project");
        t.Write("profile/.codex/config.toml", "profile = 'work'\n");
        t.Write("profile/.codex/work.config.toml", "model_instructions_file = '" + profileInstructions.Replace("\\", "\\\\") + "'\n");
        t.Write("repo/demo/.codex/config.toml", "model_instructions_file = '" + projectInstructions.Replace("\\", "\\\\") + "'\n");
        t.Write("profile/.codex/projects.json", "{\"projects\":[{\"rootPaths\":[\"" + Json(project) + "\"]}]}" );

        var result = await new DiscoveryService().ScanAsync(profile);

        Assert.Contains(result.Items, x => x.Kind == SourceKind.Environment && x.Path == Path.GetFullPath(profileInstructions) && x.Required);
        Assert.Contains(result.Items, x => x.Kind == SourceKind.Environment && x.Path == Path.GetFullPath(projectInstructions) && x.Required);
    }

    [Fact]
    public async Task ConfigPathArraysCanSpanLinesAndRetainEachExternalReference()
    {
        using var t = new TestTree();
        var profile = t.Dir("profile");
        t.Dir("profile/.codex");
        var first = t.Write("external/one.md", "one");
        var second = t.Write("external/two.md", "two");
        t.Write("profile/.codex/config.toml", "js_repl_node_module_dirs = [\n  '" + first.Replace("\\", "\\\\") + "',\n  '" + second.Replace("\\", "\\\\") + "',\n]\n");

        var result = await new DiscoveryService().ScanAsync(profile);

        Assert.Contains(result.Items, x => x.Kind == SourceKind.Environment && x.Path == Path.GetFullPath(first));
        Assert.Contains(result.Items, x => x.Kind == SourceKind.Environment && x.Path == Path.GetFullPath(second));
    }

    [Fact]
    public async Task ProjectLocalConfigIsFoundThroughParentDirectoryChain()
    {
        using var t = new TestTree();
        var profile = t.Dir("profile");
        t.Dir("profile/.codex");
        var project = t.Dir("workspace/team/app");
        var instructions = t.Write("workspace/team/instructions.md", "instructions");
        t.Write("workspace/team/.codex/config.toml", "model_instructions_file = '" + instructions.Replace("\\", "\\\\") + "'\n");
        t.Write("profile/.codex/.codex-global-state.json", "{\"projectSources\":[\"" + Json(project) + "\"]}");

        var result = await new DiscoveryService().ScanAsync(profile);

        Assert.Contains(result.Items, x => x.Kind == SourceKind.Environment && x.Path == Path.GetFullPath(instructions) && x.Required);
    }

    [Fact]
    public async Task OfficialHistoryAndRuntimeDatabasesAreListedAsRequiredSources()
    {
        using var t = new TestTree();
        var profile = t.Dir("profile");
        var core = t.Dir("profile/.codex");
        t.Write("profile/.codex/history.jsonl", "{}");
        foreach (var name in new[] { "state_5.sqlite", "logs_2.sqlite", "goals_1.sqlite", "memories_1.sqlite", "memories_v2_1.sqlite", "queue_1.sqlite", "thread_history_1.sqlite" })
            t.Write("profile/.codex/" + name, "not-a-database");

        var result = await new DiscoveryService().ScanAsync(profile);

        Assert.Contains(result.Items, x => x.Kind == SourceKind.Session && x.Path == Path.GetFullPath(Path.Combine(core, "history.jsonl")) && x.Required);
        Assert.Contains(result.Items, x => x.Kind == SourceKind.Environment && x.Path == Path.GetFullPath(Path.Combine(core, "state_5.sqlite")) && x.Required);
        Assert.Contains(result.Items, x => x.Kind == SourceKind.Environment && x.Path == Path.GetFullPath(Path.Combine(core, "memories_v2_1.sqlite")) && x.Required);
    }

    [Fact]
    public async Task SessionLifecycleDistinguishesActiveArchivedAndUnknownRoots()
    {
        using var t = new TestTree();
        var profile = t.Dir("profile");
        var core = t.Dir("profile/.codex");
        var project = t.Dir("repos/demo");
        t.Write("profile/.codex/sessions/active.jsonl", "{\"type\":\"session_meta\",\"payload\":{\"id\":\"active\",\"title\":\"当前会话\",\"cwd\":\"" + Json(project) + "\"}}\n");
        t.Write("profile/.codex/archived_sessions/archived.jsonl", "{\"type\":\"session_meta\",\"payload\":{\"id\":\"archived\",\"title\":\"归档会话\",\"cwd\":\"" + Json(project) + "\"}}\n");
        var custom = t.Dir("external/custom-sessions");
        t.Write("external/custom-sessions/unknown.jsonl", "{\"type\":\"session_meta\",\"payload\":{\"id\":\"unknown\",\"title\":\"未知会话\",\"cwd\":\"" + Json(project) + "\"}}\n");
        t.Write("profile/.codex/config.toml", "sessions_dir = '" + custom.Replace("\\", "\\\\") + "'\n");

        var result = await new DiscoveryService().ScanAsync(profile);

        Assert.Equal(SessionLifecycle.Active, Assert.Single(result.Sessions, x => x.Id == "active").Lifecycle);
        Assert.Equal(SessionLifecycle.Archived, Assert.Single(result.Sessions, x => x.Id == "archived").Lifecycle);
        Assert.Equal(SessionLifecycle.Unknown, Assert.Single(result.Sessions, x => x.Id == "unknown").Lifecycle);
    }

    [Fact]
    public async Task DuplicateSessionAssociationsRemainInManifestButCanBeCountedById()
    {
        using var t = new TestTree();
        var profile = t.Dir("profile");
        var core = t.Dir("profile/.codex");
        var projectA = t.Dir("repos/a");
        var projectB = t.Dir("repos/b");
        t.Write("profile/.codex/sessions/a.jsonl", "{\"type\":\"session_meta\",\"payload\":{\"id\":\"same\",\"title\":\"同一会话\",\"cwd\":\"" + Json(projectA) + "\"}}\n");
        t.Write("profile/.codex/sessions/b.jsonl", "{\"type\":\"session_meta\",\"payload\":{\"id\":\"same\",\"title\":\"同一会话\",\"cwd\":\"" + Json(projectB) + "\"}}\n");

        var result = await new DiscoveryService().ScanAsync(profile);

        Assert.Equal(2, result.SessionAssociationCount);
        Assert.Equal(1, result.UniqueSessionCount);
        Assert.All(result.Sessions, session => Assert.Equal(SessionLifecycle.Active, session.Lifecycle));
    }

    [Fact]
    public async Task SkillAndMarketplaceLocalPathReferencesAreDiscoveredWithoutTreatingUrlsAsFiles()
    {
        using var t = new TestTree();
        var profile = t.Dir("profile");
        t.Dir("profile/.codex");
        var skill = t.Write("external/skills/custom/SKILL.md", "# skill");
        var marketplace = t.Dir("external/marketplace");
        var extendedMarketplace = @"\\?\" + marketplace;
        t.Write("profile/.codex/config.toml", "[skills]\n[skills.'" + skill.Replace("\\", "\\\\") + "']\nenabled = true\n[marketplaces.local]\nsource = '" + extendedMarketplace.Replace("\\", "\\\\") + "'\n[marketplaces.remote]\nsource = 'https://example.invalid/marketplace'\n");

        var result = await new DiscoveryService().ScanAsync(profile);

        Assert.Contains(result.Items, x => x.Kind == SourceKind.Skill && x.Path == Path.GetFullPath(skill) && x.Required);
        Assert.Contains(result.Items, x => x.Kind == SourceKind.Plugin && x.Path == Path.GetFullPath(marketplace) && x.Required);
        Assert.DoesNotContain(result.Items, x => x.Path.Contains("example.invalid", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OversizedTomlReportsThatTheConfigurationWasNotFullyRead()
    {
        using var t = new TestTree();
        var profile = t.Dir("profile");
        t.Dir("profile/.codex");
        var lines = string.Join(Environment.NewLine, Enumerable.Repeat("model = 'gpt-test'", 100001));
        t.Write("profile/.codex/config.toml", lines);

        var result = await new DiscoveryService().ScanAsync(profile);

        Assert.Contains(result.Findings, x => x.Code == "config-line-limit" && x.Path!.EndsWith("config.toml", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OfficialSkillsConfigArrayPathIsDiscovered()
    {
        using var t = new TestTree();
        var profile = t.Dir("profile");
        t.Dir("profile/.codex");
        var skill = t.Write("external/official/SKILL.md", "# official");
        t.Write("profile/.codex/config.toml", "[[skills.config]]\npath = '" + skill.Replace("\\", "\\\\") + "'\nenabled = true\n");

        var result = await new DiscoveryService().ScanAsync(profile);

        Assert.Contains(result.Items, x => x.Kind == SourceKind.Skill && x.Path == Path.GetFullPath(skill) && x.Required);
    }

    [Fact]
    public async Task GlobalRuntimeRootFieldsAreRecognizedButWritablePermissionRootsAreIgnored()
    {
        using var t = new TestTree(); var profile = t.Dir("profile"); t.Dir("profile/.codex");
        var project = t.Dir("runtime-project"); var unrelated = t.Dir("permission-root");
        t.Write("profile/.codex/.codex-global-state.json", "{\"runtimeWorkspaceRoots\":[\"" + Json(project) + "\"],\"permissions\":{\"writableRoots\":[\"" + Json(unrelated) + "\"]}}");

        var result = await new DiscoveryService().ScanAsync(profile);

        Assert.Contains(result.Items, x => x.Kind == SourceKind.Project && x.Path == Path.GetFullPath(project));
        Assert.DoesNotContain(result.Items, x => x.Kind == SourceKind.Project && x.Path == Path.GetFullPath(unrelated));
    }

    [Fact]
    public async Task ExplicitPathMapsExcludeArbitraryKeysAndSecretValues()
    {
        using var t = new TestTree(); var profile = t.Dir("profile"); t.Dir("profile/.codex");
        var source = t.Dir("source"); var output = t.Dir("output"); var label = t.Dir("label"); var unrelated = t.Dir("unrelated");
        t.Write("profile/.codex/.codex-global-state.json", System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["projectSources"] = new[] { source },
            ["thread-projectless-output-directories"] = new Dictionary<string, string> { ["thread"] = output },
            ["electron-workspace-root-labels"] = new Dictionary<string, string> { [label] = "Label" },
            ["secret_workspace_root_value"] = unrelated,
            ["secrets"] = new Dictionary<string, string> { [unrelated] = unrelated },
            ["rootPaths"] = new { secret = unrelated }
        }));

        var result = await new DiscoveryService().ScanAsync(profile);

        foreach (var expected in new[] { source, output, label }) Assert.Contains(result.Items, x => x.Kind == SourceKind.Project && x.Path == expected);
        Assert.DoesNotContain(result.Items, x => x.Path == unrelated);
    }

    [Fact]
    public async Task AdditionalSearchDepthLimitIsReportedAndMissingExplicitCoreStillBlocks()
    {
        using var t = new TestTree(); var profile = t.Dir("profile"); var core = t.Dir("profile/.codex");
        var missing = Path.Combine(t.Root, "missing-core"); var search = t.Dir("search");
        t.Dir("search/a/b/c/d/e/f");
        t.Write("profile/.codex/config.toml", "codex_home = '" + missing + "'");

        var result = await new DiscoveryService().ScanAsync(profile, additionalRoots: [search]);

        Assert.Contains(result.Findings, x => x.Code == "additional-depth-limit");
        Assert.Contains(result.Findings, x => x.Code == "required-codex-root-missing" && x.Path == missing);
        Assert.DoesNotContain(result.Items, x => x.Path == search);
    }

    [Fact]
    public async Task MissingSqliteTranscriptIsAFileAndLaterMetadataBlocksCompleteMigration()
    {
        using var t = new TestTree(); var profile = t.Dir("profile"); var core = t.Dir("profile/.codex");
        var missing = Path.Combine(t.Root, "gone.jsonl");
        SQLitePCL.Batteries_V2.Init();
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(core, "state.sqlite")}"))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE threads (id TEXT, rollout_path TEXT); INSERT INTO threads VALUES ('missing', $path)";
            command.Parameters.AddWithValue("$path", missing); command.ExecuteNonQuery();
        }
        t.Write("profile/.codex/sessions/later.jsonl", "{\"type\":\"message\"}\n{\"type\":\"session_meta\",\"payload\":{\"id\":\"later\"}}\n");

        var result = await new DiscoveryService().ScanAsync(profile);

        Assert.False(Assert.Single(result.Items, x => x.Kind == SourceKind.Session && x.Path == missing).IsDirectory);
        Assert.Contains(result.Findings, x => x.Code == "session-metadata-unsupported" && x.Level == FindingLevel.Blocker);
        Assert.Contains(result.Sessions, x => x.Id == "later");
    }

    [Fact]
    public async Task ExplicitReferenceToMissingDefaultRemainsRequiredWithCustomCore()
    {
        using var t = new TestTree(); var profile = t.Dir("profile"); var core = t.Dir("custom");
        var missingDefault = Path.Combine(profile, ".codex");
        t.Write("custom/config.toml", "codex_home = '" + missingDefault + "'");

        var result = await new DiscoveryService().ScanAsync(profile, additionalRoots: [core]);

        Assert.Contains(result.Items, x => x.Kind == SourceKind.Core && x.Path == missingDefault && x.Required && !x.Exists);
        Assert.Contains(result.Findings, x => x.Code == "required-codex-root-missing" && x.Path == missingDefault);
    }

    [Fact]
    public async Task ManagedRequirementsTomlIsListedForMigrationReview()
    {
        using var t = new TestTree();
        var profile = t.Dir("profile");
        var core = t.Dir("profile/.codex");
        var requirements = t.Write("profile/.codex/requirements.toml", "allow_managed_hooks_only = true\n");

        var result = await new DiscoveryService().ScanAsync(profile);

        Assert.Contains(result.EnvironmentManifest.Entries, entry => entry.DisplayName == "requirements.toml" && entry.SourcePath == Path.GetFullPath(requirements));
    }

    [Fact]
    public async Task OfficialAgentAndOtelCertificatePathsAreDiscovered()
    {
        using var t = new TestTree();
        var profile = t.Dir("profile");
        t.Dir("profile/.codex");
        var agent = t.Write("profile/.codex/agents/researcher.toml", "model = 'gpt-test'\n");
        var ca = t.Write("profile/.codex/certs/ca.pem", "ca\n");
        var client = t.Write("profile/.codex/certs/client.pem", "client\n");
        var key = t.Write("profile/.codex/certs/client.key", "key\n");
        t.Write("profile/.codex/config.toml", "[agents.researcher]\nconfig_file = 'agents/researcher.toml'\n[otel.exporter.tls]\nca_certificate = 'certs/ca.pem'\nclient_certificate = 'certs/client.pem'\nclient_private_key = 'certs/client.key'\n");

        var result = await new DiscoveryService().ScanAsync(profile);

        foreach (var expected in new[] { agent, ca, client, key })
            Assert.Contains(result.Items, x => x.Kind == SourceKind.Environment && x.Path == Path.GetFullPath(expected) && x.Required);
    }

    [Fact]
    public async Task LocalMarketplaceSingleSegmentSourceIsResolvedRelativeToConfig()
    {
        using var t = new TestTree();
        var profile = t.Dir("profile");
        var marketplace = t.Dir("profile/.codex/marketplace");
        t.Write("profile/.codex/config.toml", "[marketplaces.local]\nsource_type = 'local'\nsource = 'marketplace'\n");

        var result = await new DiscoveryService().ScanAsync(profile);

        Assert.Contains(result.Items, x => x.Kind == SourceKind.Plugin && x.Path == Path.GetFullPath(marketplace) && x.Required);
    }

    [Fact]
    public async Task DeepProjectConfigTraversalReportsBoundaryInsteadOfSilentlyStopping()
    {
        using var t = new TestTree();
        var profile = t.Dir("profile");
        t.Dir("profile/.codex");
        var relative = "deep";
        for (var i = 0; i < 40; i++) relative = Path.Combine(relative, "level" + i);
        var project = t.Dir(relative);
        var ancestor = Path.Combine("deep", "level0");
        var instructions = t.Write(Path.Combine(ancestor, "instructions.md"), "instructions\n");
        t.Dir(Path.Combine(ancestor, ".codex"));
        t.Write(Path.Combine(ancestor, ".codex", "config.toml"), "model_instructions_file = '" + instructions.Replace("\\", "\\\\") + "'\n");
        t.Write("profile/.codex/projects.json", "{\"projects\":[{\"rootPaths\":[\"" + Json(project) + "\"]}]}\n");

        var result = await new DiscoveryService().ScanAsync(profile);

        Assert.Contains(result.Findings, x => x.Code == "project-config-depth-limit");
        Assert.DoesNotContain(result.Items, x => x.Kind == SourceKind.Environment && x.Path == Path.GetFullPath(instructions));
    }

    [Fact]
    public async Task ScanCancellationIsPropagatedFromJsonMetadataTraversal()
    {
        using var t = new TestTree();
        var profile = t.Dir("profile");
        t.Dir("profile/.codex");
        var projects = string.Join(",", Enumerable.Range(0, 2000).Select(i => "{\"rootPaths\":[\"" + Json(t.Dir("projects/p" + i)) + "\"]}"));
        t.Write("profile/.codex/projects.json", "{\"projects\":[" + projects + "]}");
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress(_ => cancellation.Cancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DiscoveryService().ScanAsync(profile, progress: progress, cancellationToken: cancellation.Token));
    }

    private sealed class InlineProgress(Action<OperationProgress> action) : IProgress<OperationProgress>
    {
        public void Report(OperationProgress value) => action(value);
    }

    private static string Json(string value) => value.Replace("\\", "\\\\");
}
