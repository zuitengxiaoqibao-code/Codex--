using CodexBackup.Core;
using Xunit;

namespace CodexBackup.Tests;

public class SystemTargetTests
{
    [Fact]
    public void RejectsProgramFilesDescendantsWithoutWriting()
    {
        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            var path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(path)) Assert.Throws<BackupException>(() => PathSafety.RejectSystemTarget(Path.Combine(path, "SyntheticNeverCreated")));
        }
    }

    [Fact]
    public void RejectsProgramDataDescendantsSoManagedCodexConfigIsNeverAutoActivated()
    {
        var path = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrEmpty(path)) Assert.Throws<BackupException>(() => PathSafety.RejectSystemTarget(Path.Combine(path, "OpenAI", "Codex", "config.toml")));
    }
}
