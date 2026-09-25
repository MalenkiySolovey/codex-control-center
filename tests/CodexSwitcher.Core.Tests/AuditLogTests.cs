using CodexSwitcher.Core.Tests.TestSupport;

namespace CodexSwitcher.Core.Tests;

public sealed class AuditLogTests
{
    [Fact]
    public void Record_AppendsLines_WithActionAndOutcome()
    {
        using var dir = new TempDir();
        var log = new FileAuditLog(dir.Combine("audit.log"));

        log.Record("switch", "ok", "A -> B");
        log.Record("refresh", "needs-relogin", "B");

        var lines = File.ReadAllLines(dir.Combine("audit.log"));
        Assert.Equal(2, lines.Length);
        Assert.Contains("switch", lines[0]);
        Assert.Contains("ok", lines[0]);
        Assert.Contains("refresh", lines[1]);
    }

    [Fact]
    public void Record_SanitizesTabsAndNewlines()
    {
        using var dir = new TempDir();
        var log = new FileAuditLog(dir.Combine("audit.log"));

        log.Record("switch", "ok", "line1\nline2\tcol");

        var lines = File.ReadAllLines(dir.Combine("audit.log"));
        Assert.Single(lines); // detalhe não quebra em várias linhas
    }

    [Fact]
    public void Record_rotates_audit_log_using_bounded_generations()
    {
        using var dir = new TempDir();
        var path = dir.Combine("audit.log");
        var unrelated = dir.Combine("audit.log.backup");
        File.WriteAllText(unrelated, "keep");
        var log = new FileAuditLog(path);

        var large = new string('x', (int)FileAuditLog.MaxFileBytes + 64);
        for (var index = 0; index < 260; index++)
        {
            log.Record("large", "ok", large);
        }
        log.Record("next", "ok", "small record");

        Assert.True(File.Exists(path));
        Assert.True(File.Exists(path + ".1"));
        Assert.Equal("keep", File.ReadAllText(unrelated));
        var retained = Directory.GetFiles(dir.Root)
            .Where(file => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(file), @"^audit\.log(?:\.\d+)?$"))
            .ToArray();
        Assert.Equal(FileAuditLog.RetainedRotatedFiles + 1, retained.Length);
        Assert.All(retained, file => Assert.True(new FileInfo(file).Length <= FileAuditLog.MaxFileBytes));
        Assert.Equal(FileAuditLog.RetainedRotatedFiles, Directory.GetFiles(dir.Root)
            .Count(file => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(file), @"^audit\.log\.\d+$")));
    }

    [Fact]
    public void Record_does_not_grow_audit_log_without_bound_when_rotation_is_locked()
    {
        using var dir = new TempDir();
        var path = dir.Combine("audit.log");
        File.WriteAllBytes(path, new byte[FileAuditLog.MaxFileBytes]);
        var oldestGeneration = path + "." + FileAuditLog.RetainedRotatedFiles;
        File.WriteAllText(oldestGeneration, "retained");
        using var lockedGeneration = new FileStream(
            oldestGeneration,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        var log = new FileAuditLog(path);

        log.Record("blocked", "rotation-locked", "bounded retention must win");

        Assert.Equal(FileAuditLog.MaxFileBytes, new FileInfo(path).Length);
        Assert.True(File.Exists(oldestGeneration));
    }
}
