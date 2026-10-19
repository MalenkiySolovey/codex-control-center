namespace CodexSwitcher.Infra.AgentState.Shadow;

public sealed record AccountStateShadowSettings(bool Enabled, string? DaemonExecutablePath, string StartupCategory)
{
    public const string OptInEnvironmentVariable = "CODEX_CONTROL_CENTER_AGENT_STATE_SHADOW";
    public const string ExecutableEnvironmentVariable = "CODEX_CONTROL_CENTER_AGENT_STATE_DAEMON";

    public static AccountStateShadowSettings FromEnvironment() => Parse(
        Environment.GetEnvironmentVariable(OptInEnvironmentVariable),
        Environment.GetEnvironmentVariable(ExecutableEnvironmentVariable));

    public static AccountStateShadowSettings Parse(string? optIn, string? executablePath)
    {
        if (!string.Equals(optIn, "1", StringComparison.Ordinal))
            return new AccountStateShadowSettings(false, null, "disabled");

        if (string.IsNullOrWhiteSpace(executablePath) || !Path.IsPathFullyQualified(executablePath) ||
            !string.Equals(Path.GetExtension(executablePath), ".exe", StringComparison.OrdinalIgnoreCase) ||
            IsNetworkPath(executablePath))
        {
            return new AccountStateShadowSettings(false, null, "configuration_invalid");
        }

        try
        {
            return new AccountStateShadowSettings(true, Path.GetFullPath(executablePath), "enabled");
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or PathTooLongException)
        {
            return new AccountStateShadowSettings(false, null, "configuration_invalid");
        }
    }

    private static bool IsNetworkPath(string path)
    {
        if (path.StartsWith("\\\\", StringComparison.Ordinal))
            return true;

        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            var root = Path.GetPathRoot(path);
            return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (IOException)
        {
            return true;
        }
    }
}
