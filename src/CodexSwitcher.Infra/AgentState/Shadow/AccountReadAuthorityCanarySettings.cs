namespace CodexSwitcher.Infra.AgentState.Shadow;

public sealed record AccountReadAuthorityCanarySettings(bool Requested, bool Enabled, string StartupCategory)
{
    public const string OptInEnvironmentVariable = "CODEX_CONTROL_CENTER_AGENT_STATE_READ_AUTHORITY_CANARY";

    public static AccountReadAuthorityCanarySettings FromEnvironment(AccountStateShadowSettings shadowSettings) =>
        Parse(Environment.GetEnvironmentVariable(OptInEnvironmentVariable), shadowSettings.Enabled);

    public static AccountReadAuthorityCanarySettings Parse(string? optIn, bool shadowEnabled)
    {
        var requested = string.Equals(optIn, "1", StringComparison.Ordinal);
        if (!requested)
            return new AccountReadAuthorityCanarySettings(false, false, "off");

        return shadowEnabled
            ? new AccountReadAuthorityCanarySettings(true, true, "pending_initial_sync")
            : new AccountReadAuthorityCanarySettings(true, false, "shadow_disabled");
    }
}
