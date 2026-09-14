namespace CodexBackup.Core;

public sealed record SessionProjectGroup
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public string ProjectPath { get; init; } = "";
    public IReadOnlyList<string> SessionIds { get; init; } = [];
    public DateTimeOffset? LastActivityUtc { get; init; }
    public int ActiveCount { get; init; }
    public int ArchivedCount { get; init; }
    public int UnknownCount { get; init; }
    public int ProblemCount { get; init; }
    public int SessionCount => SessionIds.Count;
}

/// <summary>
/// Builds the project-first session view from discovery data already held in memory.
/// This component intentionally performs no filesystem probes.
/// </summary>
public static class SessionProjectGrouping
{
    public const string UnassignedKey = "__unassigned_project__";

    public static IReadOnlyList<SessionProjectGroup> Create(
        IEnumerable<SessionReference> references,
        IReadOnlySet<string>? problemSessionIds = null)
    {
        problemSessionIds ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<Entry>();

        foreach (var session in references.GroupBy(SessionKey, StringComparer.OrdinalIgnoreCase))
        {
            var values = session.ToList();
            var paths = values.Select(reference => NormalizeProjectPath(reference.ProjectPath))
                .Where(path => path.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (paths.Count == 0) paths.Add(UnassignedKey);

            var active = values.Any(reference => reference.Lifecycle == SessionLifecycle.Active);
            var archived = !active && values.Any(reference => reference.Lifecycle == SessionLifecycle.Archived);
            var unknown = values.Any(reference => reference.Lifecycle == SessionLifecycle.Unknown) || !active && !archived;
            var lastActivity = values.Max(reference => reference.LastActivityUtc);
            foreach (var path in paths)
                entries.Add(new(path, session.Key, lastActivity, active, archived, unknown, problemSessionIds.Contains(session.Key)));
        }

        return entries.GroupBy(entry => entry.ProjectKey, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var sessions = group.GroupBy(entry => entry.SessionId, StringComparer.OrdinalIgnoreCase)
                    .Select(session => session.OrderByDescending(entry => entry.LastActivityUtc ?? DateTimeOffset.MinValue).First())
                    .ToList();
                var path = group.Key == UnassignedKey ? "" : group.Key;
                return new SessionProjectGroup
                {
                    Key = group.Key,
                    Name = group.Key == UnassignedKey ? "未识别项目" : ProjectName(path),
                    ProjectPath = path,
                    SessionIds = sessions.Select(entry => entry.SessionId).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList(),
                    LastActivityUtc = sessions.Max(entry => entry.LastActivityUtc),
                    ActiveCount = sessions.Count(entry => entry.Active),
                    ArchivedCount = sessions.Count(entry => entry.Archived),
                    UnknownCount = sessions.Count(entry => entry.Unknown),
                    ProblemCount = sessions.Count(entry => entry.Problem)
                };
            })
            .OrderBy(group => group.Key == UnassignedKey ? 1 : 0)
            .ThenByDescending(group => group.LastActivityUtc ?? DateTimeOffset.MinValue)
            .ThenBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string SessionKey(SessionReference reference) =>
        string.IsNullOrWhiteSpace(reference.Id) ? reference.TranscriptPath : reference.Id;

    private static string NormalizeProjectPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    private static string ProjectName(string path)
    {
        var name = Path.GetFileName(path);
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    private sealed record Entry(
        string ProjectKey,
        string SessionId,
        DateTimeOffset? LastActivityUtc,
        bool Active,
        bool Archived,
        bool Unknown,
        bool Problem);
}
