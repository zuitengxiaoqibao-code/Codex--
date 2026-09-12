using CodexBackup.Core;
using Xunit;

namespace CodexBackup.Tests;

public sealed class GuidanceAndArtifactsTests
{
    [Fact]
    public void ExceptionGuidanceUsesCauseImpactAndActionWithoutTypeNames()
    {
        var text = UserGuidance.ExplainException(new IOException("fixture"));
        Assert.Contains("原因", text);
        Assert.Contains("影响", text);
        Assert.Contains("处理", text);
        Assert.DoesNotContain(nameof(IOException), text);
    }

    [Fact]
    public void UnverifiedTemporaryArtifactCannotBeDeleted()
    {
        using var t = new TestTree();
        var stage = t.Dir(".codex-restore-demo.stage");
        t.Write(".codex-restore-demo.stage/data.txt", "fixture");
        Assert.Throws<BackupException>(() => TemporaryArtifactManager.ValidateForCleanup(stage, verified: false));
        Assert.True(Directory.Exists(stage));

        TemporaryArtifactManager.DeleteAfterVerification(stage, verified: true);
        Assert.False(Directory.Exists(stage));
    }
}
