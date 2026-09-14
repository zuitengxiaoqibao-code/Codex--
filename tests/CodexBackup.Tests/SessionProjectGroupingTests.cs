using CodexBackup.Core;
using Xunit;

namespace CodexBackup.Tests;

public class SessionProjectGroupingTests
{
    [Fact]
    public void SameDirectoryMergesSessionsAndIgnoresPathCasing()
    {
        var path = Path.Combine(Path.GetTempPath(), "CodexBackup", "Chat");
        var groups = SessionProjectGrouping.Create(
        [
            Session("one", path, SessionLifecycle.Active),
            Session("two", path.ToUpperInvariant(), SessionLifecycle.Archived)
        ]);

        var group = Assert.Single(groups);
        Assert.Equal("Chat", group.Name);
        Assert.Equal(Path.GetFullPath(path), group.ProjectPath, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(["one", "two"], group.SessionIds.OrderBy(id => id));
        Assert.Equal(1, group.ActiveCount);
        Assert.Equal(1, group.ArchivedCount);
    }

    [Fact]
    public void MissingProjectsShareOneFinalGroup()
    {
        var recent = DateTimeOffset.Parse("2026-09-14T10:00:00Z");
        var groups = SessionProjectGrouping.Create(
        [
            Session("missing-a", "", SessionLifecycle.Unknown, recent),
            Session("known", Path.Combine(Path.GetTempPath(), "known"), SessionLifecycle.Active, recent.AddHours(-1)),
            Session("missing-b", " ", SessionLifecycle.Unknown, recent.AddHours(-2))
        ]);

        Assert.Equal(2, groups.Count);
        var missing = groups[^1];
        Assert.Equal(SessionProjectGrouping.UnassignedKey, missing.Key);
        Assert.Equal("未识别项目", missing.Name);
        Assert.Equal(2, missing.SessionCount);
        Assert.Equal(2, missing.UnknownCount);
    }

    [Fact]
    public void OneSessionCanAppearInEveryAssociatedProjectGroup()
    {
        var first = Path.Combine(Path.GetTempPath(), "repo-a");
        var second = Path.Combine(Path.GetTempPath(), "repo-b");
        var groups = SessionProjectGrouping.Create(
        [
            Session("shared", first, SessionLifecycle.Active),
            Session("shared", second, SessionLifecycle.Active),
            Session("only-a", first, SessionLifecycle.Active)
        ]);

        Assert.Equal(2, groups.Count);
        Assert.Contains(groups, group => group.ProjectPath.Equals(Path.GetFullPath(first), StringComparison.OrdinalIgnoreCase)
            && group.SessionIds.OrderBy(id => id).SequenceEqual(new[] { "only-a", "shared" }));
        Assert.Contains(groups, group => group.ProjectPath.Equals(Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase)
            && group.SessionIds.SequenceEqual(new[] { "shared" }));
    }

    [Fact]
    public void GroupsSortByLatestActivityAndReportProblemsOncePerSession()
    {
        var oldPath = Path.Combine(Path.GetTempPath(), "old-project");
        var newPath = Path.Combine(Path.GetTempPath(), "new-project");
        var groups = SessionProjectGrouping.Create(
        [
            Session("old", oldPath, SessionLifecycle.Active, DateTimeOffset.Parse("2026-01-01T00:00:00Z")),
            Session("new", newPath, SessionLifecycle.Archived, DateTimeOffset.Parse("2026-09-01T00:00:00Z"), missing: true),
            Session("new", newPath, SessionLifecycle.Archived, DateTimeOffset.Parse("2026-09-02T00:00:00Z"), missing: true)
        ], new HashSet<string>(["new"], StringComparer.OrdinalIgnoreCase));

        Assert.Equal("new-project", groups[0].Name);
        Assert.Equal(1, groups[0].SessionCount);
        Assert.Equal(1, groups[0].ProblemCount);
        Assert.Equal(DateTimeOffset.Parse("2026-09-02T00:00:00Z"), groups[0].LastActivityUtc);
    }

    private static SessionReference Session(
        string id,
        string path,
        SessionLifecycle lifecycle,
        DateTimeOffset? activity = null,
        bool missing = false) => new()
        {
            Id = id,
            ProjectPath = path,
            TranscriptPath = missing ? "" : Path.Combine(Path.GetTempPath(), id + ".jsonl"),
            Lifecycle = lifecycle,
            LastActivityUtc = activity
        };
}
