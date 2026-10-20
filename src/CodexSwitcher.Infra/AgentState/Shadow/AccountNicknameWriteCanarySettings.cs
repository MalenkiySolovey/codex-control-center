namespace CodexSwitcher.Infra.AgentState.Shadow;

public sealed record AccountNicknameWriteCanarySettings(bool Requested, bool Enabled, string StartupCategory)
{
    public const string OptInEnvironmentVariable = "CODEX_CONTROL_CENTER_AGENT_STATE_NICKNAME_WRITE_CANARY";

    public static AccountNicknameWriteCanarySettings FromEnvironment(
        AccountStateShadowSettings shadowSettings,
        AccountReadAuthorityCanarySettings readSettings) =>
        Parse(
            Environment.GetEnvironmentVariable(OptInEnvironmentVariable),
            shadowSettings.Enabled,
            readSettings.Enabled);

    public static AccountNicknameWriteCanarySettings Parse(
        string? optIn,
        bool shadowEnabled,
        bool readAuthorityCanaryEnabled)
    {
        var requested = string.Equals(optIn, "1", StringComparison.Ordinal);
        if (!requested)
            return new AccountNicknameWriteCanarySettings(false, false, "off");

        if (!shadowEnabled)
            return new AccountNicknameWriteCanarySettings(true, false, "shadow_disabled");

        if (!readAuthorityCanaryEnabled)
            return new AccountNicknameWriteCanarySettings(true, false, "read_authority_canary_disabled");

        return new AccountNicknameWriteCanarySettings(true, true, "enabled");
    }
}
