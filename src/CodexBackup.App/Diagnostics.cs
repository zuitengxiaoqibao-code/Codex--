using System.IO;
using System.Text;
using System.Text.Json;
using CodexBackup.Core;

namespace CodexBackup.App;

internal static class Diagnostics
{
    internal static async Task<int> RunAsync(string mode, string output)
    {
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        try
        {
            object report;
            if (mode == "--scan-report")
            {
                var scan = await new DiscoveryService().ScanAsync();
                report = new { mode, success = true, scan };
            }
            else
            {
                var root = Path.Combine(Path.GetDirectoryName(output)!, "self-test-" + Guid.NewGuid().ToString("N"));
                var source = Path.Combine(root, "source"); Directory.CreateDirectory(Path.Combine(source, "空目录"));
                var original = Path.Combine(source, "中文😀.txt"); await File.WriteAllTextAsync(original, "Codex 文件往返校验\n", new UTF8Encoding(false));
                var engine = new BackupEngine();
                var backup = await engine.BackupAsync(new() { Sources = [new() { Path = source, Name = "合成测试数据", Kind = SourceKind.Custom, Exists = true, Required = true }], DestinationDirectory = Path.Combine(root, "backups") });
                var verified = await new PackageVerifier().VerifyAsync(backup.PackagePath);
                var target = Path.Combine(root, "target"); Directory.CreateDirectory(target); await File.WriteAllTextAsync(Path.Combine(target, "previous.txt"), "existing data");
                var restore = new RestoreEngine();
                var request = new RestoreRequest { PackagePath = backup.PackagePath, ReplaceExisting = true, Mappings = [new() { RootId = backup.Manifest.Roots.Single().Id, TargetPath = target }] };
                if (!(await restore.PreviewAsync(request)).CanProceed) throw new Exception("恢复预演意外阻止了合成数据测试。");
                var restored = await restore.RestoreAsync(request);
                if (await File.ReadAllTextAsync(Path.Combine(target, "中文😀.txt")) != await File.ReadAllTextAsync(original) || !Directory.Exists(Path.Combine(target, "空目录"))) throw new Exception("文件往返不一致。");
                await restore.RollbackAsync(restored.JournalPath);
                if (await File.ReadAllTextAsync(Path.Combine(target, "previous.txt")) != "existing data") throw new Exception("回滚内容不一致。");
                var payload = Path.Combine(backup.PackagePath, "payload", verified.Files.Single(f => !f.IsDirectory).Id);
                await File.WriteAllTextAsync(payload, "injected corruption");
                var rejected = false;
                try { await new PackageVerifier().VerifyAsync(backup.PackagePath); } catch (BackupException) { rejected = true; }
                if (!rejected) throw new Exception("损坏备份未被拒绝。");
                report = new { mode, success = true, runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(), fixtureRoot = root, checks = new[] { "Unicode and empty-directory backup", "SQLite inventory and SHA-256", "restore preview", "replacement with original preservation", "sealed rollback", "corruption rejection" }, limitation = "使用合成目录验证实际发行程序的核心往返；不是新装系统或真实 Codex 恢复验收。测试包已故意损坏。" };
            }
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            return 0;
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { mode, success = false, error = ex.GetType().Name, message = ex.Message }, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            return 1;
        }
    }
}
