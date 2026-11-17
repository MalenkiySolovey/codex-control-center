namespace CodexSwitcher.Infra.AgentState.Shadow;

public sealed record NicknameAuthorityCanarySettings(
    bool Requested,
    string? DaemonExecutablePath,
    string StartupCategory)
{
    public const string OptInEnvironmentVariable = "CODEX_CONTROL_CENTER_AGENT_STATE_NICKNAME_AUTHORITY_CANARY";

    public static NicknameAuthorityCanarySettings FromEnvironment() => Parse(
        Environment.GetEnvironmentVariable(OptInEnvironmentVariable),
        Environment.GetEnvironmentVariable(AccountStateShadowSettings.ExecutableEnvironmentVariable));

    public static NicknameAuthorityCanarySettings Parse(string? optIn, string? executablePath)
    {
        var requested = string.Equals(optIn, "1", StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(executablePath) || !Path.IsPathFullyQualified(executablePath) ||
            !string.Equals(Path.GetExtension(executablePath), ".exe", StringComparison.OrdinalIgnoreCase) ||
            AccountStateShadowSettings.IsNetworkPath(executablePath))
        {
            return new NicknameAuthorityCanarySettings(requested, null, requested ? "configuration_invalid" : "off");
        }

        try
        {
            return new NicknameAuthorityCanarySettings(requested, Path.GetFullPath(executablePath), requested ? "enabled" : "off");
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or PathTooLongException)
        {
            return new NicknameAuthorityCanarySettings(requested, null, requested ? "configuration_invalid" : "off");
        }
    }

    public bool CanStartDaemon => DaemonExecutablePath is not null;
    public bool Enabled => Requested && CanStartDaemon;
}
