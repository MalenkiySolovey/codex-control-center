using System.Text.Json;
using System.Text.Json.Serialization;
using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Settings.Models;
using CodexSwitcher.Core.Usage.Models;
using CodexSwitcher.Infra.Accounts.Storage;
using CodexSwitcher.Infra.Codex.Usage;
using CodexSwitcher.Infra.Codex.Routing;
using CodexSwitcher.Infra.Common.Paths;
using CodexSwitcher.Infra.Common.Storage;
using CodexSwitcher.Infra.Providers.Storage;
using CodexSwitcher.Infra.Settings;

namespace CodexSwitcher.Cli;

internal static class Program
{
    private const string ExportSchemaResourceName = "CodexSwitcher.Cli.codex-switchboard-cli-export-v1.schema.json";
    private const string FleetSchemaResourceName = "CodexSwitcher.Cli.codex-switchboard-cli-fleet-v1.schema.json";
    private const string PowerShellCompletionResourceName = "CodexSwitcher.Cli.codex-switchboard.ps1";
    private const string BashCompletionResourceName = "CodexSwitcher.Cli.codex-switchboard.bash";
    private const string ZshCompletionResourceName = "CodexSwitcher.Cli.codex-switchboard.zsh";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static int Main(string[] args)
    {
        var command = args.FirstOrDefault()?.Trim().ToLowerInvariant();
        var json = args.Skip(1).Any(argument => argument.Equals("--json", StringComparison.OrdinalIgnoreCase));
        if (command is null or "help" or "--help" or "-h")
        {
            PrintUsage();
            return 0;
        }

        if (command == "schema")
        {
            var schemaKind = args.Length == 1 ? "export" : args.Length == 2 ? args[1].Trim().ToLowerInvariant() : string.Empty;
            if (schemaKind is not ("export" or "fleet"))
            {
                PrintUsage(Console.Error);
                return 2;
            }

            try
            {
                PrintSchema(schemaKind);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"CLI schema unavailable ({ex.GetType().Name}).");
                return 1;
            }
        }

        if (command == "completion")
        {
            var shell = args.Length == 2 ? args[1].Trim().ToLowerInvariant() : string.Empty;
            if (shell is not ("powershell" or "bash" or "zsh"))
            {
                PrintUsage(Console.Error);
                return 2;
            }

            try
            {
                PrintCompletion(shell);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"CLI completion unavailable ({ex.GetType().Name}).");
                return 1;
            }
        }

        if (args.Skip(1).Any(argument => !argument.Equals("--json", StringComparison.OrdinalIgnoreCase)))
        {
            PrintUsage(Console.Error);
            return 2;
        }

        if (command is not ("list" or "current" or "status" or "export" or "fleet"))
        {
            PrintUsage(Console.Error);
            return 2;
        }

        try
        {
            if (command == "fleet")
            {
                PrintFleet(ReadFleetSnapshot(), json);
                return 0;
            }

            var snapshot = ReadSnapshot();
            switch (command)
            {
                case "list":
                    PrintList(snapshot, json);
                    break;
                case "current":
                    PrintCurrent(snapshot.CurrentTarget, json);
                    break;
                case "status":
                    PrintStatus(snapshot, json);
                    break;
                case "export":
                    PrintExport(snapshot);
                    break;
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"CLI command failed ({ex.GetType().Name}). No credentials were accessed.");
            return 1;
        }
    }

    private static void PrintSchema(string kind)
    {
        var resourceName = kind == "fleet" ? FleetSchemaResourceName : ExportSchemaResourceName;
        using var schema = typeof(Program).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("The requested CLI JSON Schema is unavailable.");
        schema.CopyTo(Console.OpenStandardOutput());
    }

    private static void PrintCompletion(string shell)
    {
        var resourceName = shell switch
        {
            "powershell" => PowerShellCompletionResourceName,
            "bash" => BashCompletionResourceName,
            "zsh" => ZshCompletionResourceName,
            _ => throw new ArgumentOutOfRangeException(nameof(shell)),
        };
        using var completion = typeof(Program).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("The requested CLI completion script is unavailable.");
        completion.CopyTo(Console.OpenStandardOutput());
    }

    private static ReadOnlySnapshot ReadSnapshot()
    {
        var paths = new AppPaths();
        var fileSystem = new PhysicalFileSystem();
        var accounts = ReadSavedAccounts(fileSystem, paths);
        var providers = new ApiProviderStore(fileSystem, paths.ApiProvidersPath)
            .ReadAllReadOnly()
            .OrderBy(profile => profile.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(profile => new ProviderRow(
                profile.DisplayName,
                profile.Status,
                profile.GetEffectiveSelectedModel(),
                profile.ModelInventory?.GetEnabledModels().Count() ?? 0,
                profile.StableCodexProviderId))
            .ToArray();
        var settings = new SettingsStore(fileSystem, paths.SettingsPath).Load();
        var route = new CodexRoutingConfigStore(fileSystem, paths)
            .ReadRoutingState(paths.Codex.ConfigTomlPath);

        var currentTarget = ResolveCurrentTarget(route, providers);
        return new ReadOnlySnapshot(accounts, providers, settings.CodexLaunchProfiles?.Count ?? 0, currentTarget);
    }

    private static AccountRow[] ReadSavedAccounts(PhysicalFileSystem fileSystem, AppPaths paths) =>
        new ProfileStore(fileSystem, paths.ProfilesPath)
            .ReadAllReadOnly()
            .Where(profile => !string.Equals(profile.AuthMode, "apikey", StringComparison.OrdinalIgnoreCase))
            .OrderBy(profile => profile.SortOrder)
            .Select((profile, index) => new AccountRow(profile.Id, GetSafeAccountLabel(profile, index + 1)))
            .ToArray();

    private static FleetSnapshot ReadFleetSnapshot()
    {
        var paths = new AppPaths();
        var fileSystem = new PhysicalFileSystem();
        var accounts = ReadSavedAccounts(fileSystem, paths);
        using var usageCache = new UsageCache(fileSystem, paths.UsageCachePath);
        usageCache.LoadAsync().GetAwaiter().GetResult();

        var rows = accounts.Select(account =>
        {
            var entry = usageCache.Get(account.ProfileId);
            var snapshot = entry?.Snapshot;
            var windows = snapshot?.PrimaryWindows
                .Select(window => new FleetWindow(
                    window.DisplayLabel,
                    window.DurationMinutes,
                    NormalizePercent(window.RemainingPercent),
                    window.ResetsAt))
                .ToArray() ?? [];

            return new FleetAccount(
                account.Name,
                entry?.Status.ToString() ?? UsageStatus.Unknown.ToString(),
                entry is null ? null : entry.IsStale,
                entry?.ObservedAt,
                snapshot is null ? null : IsRateLimited(snapshot, entry!.Status),
                windows);
        }).ToArray();

        var snapshotRows = rows.Where(row => row.RateLimited is not null).ToArray();
        var rateLimitedCount = snapshotRows.Count(row => row.RateLimited == true);
        return new FleetSnapshot(
            rows,
            new FleetSummary(
                SavedAccountCount: rows.Length,
                CachedSnapshotCount: snapshotRows.Length,
                RateLimitedCachedSnapshotCount: rateLimitedCount));
    }

    private static void PrintFleet(FleetSnapshot snapshot, bool json)
    {
        if (json)
        {
            WriteJson(new
            {
                schemaVersion = 1,
                source = "local-cache",
                summary = snapshot.Summary,
                accounts = snapshot.Accounts,
            });
            return;
        }

        Console.WriteLine("ChatGPT account fleet (local cache only; no network refresh)");
        Console.WriteLine($"Saved accounts: {snapshot.Summary.SavedAccountCount}");
        Console.WriteLine($"Cached quota snapshots: {snapshot.Summary.CachedSnapshotCount}");
        Console.WriteLine($"Rate-limited cached snapshots: {snapshot.Summary.RateLimitedCachedSnapshotCount}");

        foreach (var account in snapshot.Accounts)
        {
            var freshness = account.IsStale switch
            {
                true when account.RateLimited is not null => "stale cache",
                false when account.RateLimited is not null => "not marked stale",
                _ => "no quota snapshot",
            };
            var status = account.RateLimited switch
            {
                true => "rate limited",
                false => account.Status,
                _ => account.Status,
            };
            var windows = account.Windows.Count == 0
                ? "no cached windows"
                : string.Join(" · ", account.Windows.Select(FormatFleetWindow));
            var observed = account.ObservedAt?.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture)
                ?? "not observed";
            Console.WriteLine($"- {account.Name} · {status} · {freshness} · observed {observed} · {windows}");
        }
    }

    private static string FormatFleetWindow(FleetWindow window)
    {
        var remaining = window.RemainingPercent is { } percent
            ? $"{percent:0.#}% remaining"
            : "remaining unknown";
        var reset = window.ResetsAt?.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture)
            ?? "reset unknown";
        return $"{window.Label}: {remaining}, resets {reset}";
    }

    private static bool IsRateLimited(RateLimitsSnapshot snapshot, UsageStatus status) =>
        snapshot.OrdinaryUsageAllowed == false ||
        status == UsageStatus.RateLimited ||
        !string.IsNullOrWhiteSpace(snapshot.PrimaryBucket?.RateLimitReachedType);

    private static double? NormalizePercent(double? value) =>
        value is { } percent && double.IsFinite(percent) && percent is >= 0.0 and <= 100.0
            ? percent
            : null;

    private static string GetSafeAccountLabel(ProfileMetadata profile, int ordinal)
    {
        if (!string.IsNullOrWhiteSpace(profile.Nickname) && !LooksLikeEmail(profile.Nickname))
        {
            return profile.Nickname.Trim();
        }

        if (!string.IsNullOrWhiteSpace(profile.DetectedDisplayName) && !LooksLikeEmail(profile.DetectedDisplayName))
        {
            return profile.DetectedDisplayName.Trim();
        }

        // Avoid printing the account email or an auth/token-derived identifier.
        return $"ChatGPT account {ordinal}";
    }

    private static bool LooksLikeEmail(string value) => value.Contains('@');

    private static CurrentTarget ResolveCurrentTarget(
        CodexSwitcher.Core.Routing.Contracts.CodexRoutingState route,
        IReadOnlyList<ProviderRow> providers)
    {
        if (string.IsNullOrWhiteSpace(route.ModelProvider) ||
            string.Equals(route.ModelProvider, "openai", StringComparison.OrdinalIgnoreCase))
        {
            return new CurrentTarget("chatgpt", "ChatGPT", route.Model);
        }

        var provider = providers.FirstOrDefault(item =>
            string.Equals(item.StableProviderId, route.ModelProvider, StringComparison.Ordinal));
        return provider is null
            ? new CurrentTarget("external", "External Codex provider", route.Model)
            : new CurrentTarget("api-provider", provider.Name, route.Model);
    }

    private static void PrintList(ReadOnlySnapshot snapshot, bool json)
    {
        if (json)
        {
            WriteJson(new
            {
                accounts = snapshot.Accounts.Select(account => new { account.Name }),
                apiProviders = snapshot.Providers.Select(provider => new
                {
                    provider.Name,
                    status = provider.Status.ToString(),
                    provider.SelectedModel,
                    provider.EnabledModelCount,
                }),
            });
            return;
        }

        Console.WriteLine($"Saved ChatGPT accounts ({snapshot.Accounts.Count}):");
        if (snapshot.Accounts.Count == 0)
        {
            Console.WriteLine("  (none)");
        }
        foreach (var account in snapshot.Accounts)
        {
            Console.WriteLine($"  - {account.Name}");
        }

        Console.WriteLine($"API providers ({snapshot.Providers.Count}):");
        if (snapshot.Providers.Count == 0)
        {
            Console.WriteLine("  (none)");
        }
        foreach (var provider in snapshot.Providers)
        {
            var model = string.IsNullOrWhiteSpace(provider.SelectedModel) ? "no selected model" : provider.SelectedModel;
            Console.WriteLine($"  - {provider.Name} · {provider.Status} · {model} · {provider.EnabledModelCount} enabled models");
        }
    }

    private static void PrintCurrent(CurrentTarget target, bool json)
    {
        if (json)
        {
            WriteJson(target);
            return;
        }

        Console.WriteLine($"Target: {target.Name} ({target.Kind})");
        Console.WriteLine($"Model: {target.Model ?? "unknown"}");
    }

    private static void PrintStatus(ReadOnlySnapshot snapshot, bool json)
    {
        if (json)
        {
            WriteJson(new
            {
                target = snapshot.CurrentTarget,
                savedChatGptAccounts = snapshot.Accounts.Count,
                apiProviders = snapshot.Providers.Count,
                launchProfiles = snapshot.LaunchProfileCount,
            });
            return;
        }

        PrintCurrent(snapshot.CurrentTarget, json: false);
        Console.WriteLine($"Saved ChatGPT accounts: {snapshot.Accounts.Count}");
        Console.WriteLine($"API providers: {snapshot.Providers.Count}");
        Console.WriteLine($"Launch profiles: {snapshot.LaunchProfileCount}");
    }

    private static void PrintExport(ReadOnlySnapshot snapshot)
    {
        WriteJson(new
        {
            schemaVersion = 1,
            target = snapshot.CurrentTarget,
            accounts = snapshot.Accounts.Select(account => new { account.Name }),
            apiProviders = snapshot.Providers.Select(provider => new
            {
                provider.Name,
                status = provider.Status.ToString(),
                provider.SelectedModel,
                provider.EnabledModelCount,
            }),
            launchProfileCount = snapshot.LaunchProfileCount,
        });
    }

    private static void WriteJson<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value, JsonOptions));

    private static void PrintUsage(TextWriter? output = null)
    {
        output ??= Console.Out;
        output.WriteLine("Codex Switchboard read-only CLI");
        output.WriteLine("  codex-switchboard list [--json]     List saved accounts and API providers");
        output.WriteLine("  codex-switchboard current [--json]  Show the current Codex target and model");
        output.WriteLine("  codex-switchboard status [--json]   Show target and local profile counts");
        output.WriteLine("  codex-switchboard export            Write a redacted JSON snapshot to stdout");
        output.WriteLine("  codex-switchboard fleet [--json]    Show cached quota status for saved ChatGPT accounts");
        output.WriteLine("  codex-switchboard schema [export|fleet] Write a CLI JSON Schema to stdout");
        output.WriteLine("  codex-switchboard completion <powershell|bash|zsh> Print shell completion script to stdout");
        output.WriteLine("  codex-switchboard help               Show this help");
        output.WriteLine("This CLI reads local profile, routing, and usage-cache metadata only; it never accesses credentials or the network, and it does not change routing.");
    }

    private sealed record AccountRow(Guid ProfileId, string Name);

    private sealed record ProviderRow(
        string Name,
        ApiProviderProfileStatus Status,
        string? SelectedModel,
        int EnabledModelCount,
        string? StableProviderId = null);

    private sealed record CurrentTarget(string Kind, string Name, string? Model);

    private sealed record ReadOnlySnapshot(
        IReadOnlyList<AccountRow> Accounts,
        IReadOnlyList<ProviderRow> Providers,
        int LaunchProfileCount,
        CurrentTarget CurrentTarget);

    private sealed record FleetWindow(
        string Label,
        int? DurationMinutes,
        double? RemainingPercent,
        DateTimeOffset? ResetsAt);

    private sealed record FleetAccount(
        string Name,
        string Status,
        bool? IsStale,
        DateTimeOffset? ObservedAt,
        bool? RateLimited,
        IReadOnlyList<FleetWindow> Windows);

    private sealed record FleetSummary(
        int SavedAccountCount,
        int CachedSnapshotCount,
        int RateLimitedCachedSnapshotCount);

    private sealed record FleetSnapshot(
        IReadOnlyList<FleetAccount> Accounts,
        FleetSummary Summary);
}
