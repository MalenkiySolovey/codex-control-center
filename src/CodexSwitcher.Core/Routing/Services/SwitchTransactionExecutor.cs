using System.Security.Cryptography;
using System.Text.Json;
using CodexSwitcher.Core.Accounts.Formatting;
using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Accounts.Services;
using CodexSwitcher.Core.Common.Enums;
using CodexSwitcher.Core.Common.Environment;
using CodexSwitcher.Core.Common.Errors;
using CodexSwitcher.Core.Common.Lifecycle;
using CodexSwitcher.Core.Common.Logging;
using CodexSwitcher.Core.Common.Storage;
using CodexSwitcher.Core.Common.Time;
using CodexSwitcher.Core.Providers.Contracts;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Core.Routing.Models;

namespace CodexSwitcher.Core.Routing.Services;

/// <summary>
/// Production implementation of <see cref="ISwitchTransactionExecutor"/> coordinating
/// atomic resource mutations, process lifecycles, auth.json integrity verification,
/// and reverse-order multi-resource rollbacks.
/// JOURNAL_READY_BOUNDARY = YES.
/// </summary>
public sealed class SwitchTransactionExecutor : ISwitchTransactionExecutor
{
    private readonly SwitchService _chatGptSwitchService;
    private readonly ICodexRoutingConfigStore _routingConfig;
    private readonly IApiProviderStore _apiProviderStore;
    private readonly IProcessManager _processes;
    private readonly IFileSystem _fs;
    private readonly IClock _clock;
    private readonly IAuditLog _audit;
    private readonly CodexPaths _paths;
    private readonly Func<ISwitchCompensationCoordinator>? _coordinatorFactory;
    private readonly ICodexModelCatalogService? _modelCatalogService;
    private readonly ICodexRuntimeModelCatalogVerifier? _runtimeModelCatalogVerifier;
    private readonly bool? _allowGlobalProcessControlOverride;

    // Kept as a concrete overload for binary compatibility with already
    // compiled presentation/plugin assemblies from preview.18. New callers
    // use the verifier-aware constructor below; this overload preserves the
    // same behavior with no runtime model/list verifier available.
    public SwitchTransactionExecutor(
        SwitchService chatGptSwitchService,
        ICodexRoutingConfigStore routingConfig,
        IApiProviderStore apiProviderStore,
        IProcessManager processes,
        IFileSystem fs,
        IClock clock,
        IAuditLog audit,
        CodexPaths paths,
        Func<ISwitchCompensationCoordinator>? coordinatorFactory,
        ICodexModelCatalogService? modelCatalogService)
        : this(
            chatGptSwitchService,
            routingConfig,
            apiProviderStore,
            processes,
            fs,
            clock,
            audit,
            paths,
            coordinatorFactory,
            modelCatalogService,
            runtimeModelCatalogVerifier: null,
            allowGlobalProcessControlOverride: null)
    {
    }

    public SwitchTransactionExecutor(
        SwitchService chatGptSwitchService,
        ICodexRoutingConfigStore routingConfig,
        IApiProviderStore apiProviderStore,
        IProcessManager processes,
        IFileSystem fs,
        IClock clock,
        IAuditLog audit,
        CodexPaths paths,
        Func<ISwitchCompensationCoordinator>? coordinatorFactory = null,
        ICodexModelCatalogService? modelCatalogService = null,
        ICodexRuntimeModelCatalogVerifier? runtimeModelCatalogVerifier = null,
        bool? allowGlobalProcessControlOverride = null)
    {
        _chatGptSwitchService = chatGptSwitchService ?? throw new ArgumentNullException(nameof(chatGptSwitchService));
        _routingConfig = routingConfig ?? throw new ArgumentNullException(nameof(routingConfig));
        _apiProviderStore = apiProviderStore ?? throw new ArgumentNullException(nameof(apiProviderStore));
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _coordinatorFactory = coordinatorFactory;
        _modelCatalogService = modelCatalogService;
        _runtimeModelCatalogVerifier = runtimeModelCatalogVerifier;
        _allowGlobalProcessControlOverride = allowGlobalProcessControlOverride;
    }

    private ISwitchCompensationCoordinator CreateCompensationCoordinator() =>
        _coordinatorFactory != null
            ? _coordinatorFactory()
            : new SwitchCompensationCoordinator(_routingConfig, _processes, _audit, _paths);

    private bool UsesDefaultCodexHome()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
        {
            return false;
        }

        try
        {
            var configuredHome = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_paths.CodexHome));
            var defaultHome = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(userProfile, ".codex")));
            return string.Equals(configuredHome, defaultHome, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // A path that cannot be canonicalized is never safe for global process control.
            return false;
        }
    }

    private bool AllowsGlobalProcessControl() => _allowGlobalProcessControlOverride ?? UsesDefaultCodexHome();

    public async Task<TargetSwitchResult> ExecuteAsync(CodexSwitchPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        return plan switch
        {
            InvalidSwitchPlan invalidPlan => ExecuteInvalidPlan(invalidPlan),
            ChatGptNoOpSwitchPlan noOpPlan => ExecuteChatGptNoOp(noOpPlan),
            ApiProviderModelSelectionPlan selectionPlan => await ExecuteApiProviderModelSelectionAsync(selectionPlan, cancellationToken).ConfigureAwait(false),
            ApiRouteSwitchPlan routePlan => ExecuteApiRouteSwitch(routePlan),
            ApiProviderSwitchPlan apiPlan => await ExecuteApiProviderSwitchAsync(apiPlan, cancellationToken).ConfigureAwait(false),
            ChatGptAccountSwitchPlan accountPlan => await ExecuteChatGptAccountSwitchAsync(accountPlan, cancellationToken).ConfigureAwait(false),
            ChatGptReturnRoutingSwitchPlan returnPlan => await ExecuteChatGptReturnRoutingAsync(returnPlan, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unsupported switch plan type '{plan.GetType().Name}'.")
        };
    }

    private async Task<TargetSwitchResult> ExecuteApiProviderModelSelectionAsync(
        ApiProviderModelSelectionPlan plan,
        CancellationToken cancellationToken)
    {
        var profile = plan.Profile;
        var target = new ActiveTarget.Api(profile);

        if (plan.IsNoOp)
        {
            return new TargetSwitchResult(
                TargetSwitchOutcome.NoOp,
                $"Model '{plan.SelectedModel}' is already selected.",
                target,
                ModelSelectionWasActiveTarget: plan.IsActiveTarget);
        }

        if (!plan.IsActiveTarget)
        {
            profile.SetSelectedModel(plan.SelectedModel);
            try
            {
                _apiProviderStore.Save(profile);
                _audit.Record(
                    "api-model-selection",
                    "saved-inactive",
                    $"PROFILE_ID={profile.Id:D} MODEL={plan.SelectedModel} ACTIVE_ROUTE_CHANGED=false");
                return new TargetSwitchResult(
                    TargetSwitchOutcome.Success,
                    $"Selected '{plan.SelectedModel}' for {profile.DisplayName}. The active Codex route was not changed.",
                    target,
                    ModelSelectionWasActiveTarget: false);
            }
            catch (Exception ex)
            {
                profile.SetSelectedModel(plan.PreviousSelectedModel);
                try
                {
                    _apiProviderStore.Save(profile);
                }
                catch (Exception rollbackException)
                {
                    var persisted = _apiProviderStore.GetById(profile.Id)?.GetEffectiveSelectedModel();
                    return new TargetSwitchResult(
                        TargetSwitchOutcome.Failed,
                        $"Could not save model '{plan.SelectedModel}' for the inactive provider. The profile currently stores '{persisted ?? "no model"}'. Active Codex routing was not changed. Recovery save failed: {rollbackException.Message}",
                        target,
                        ErrorInfo.Create(ErrorCategory.Unknown, ex.Message, _clock.UtcNow),
                        ModelSelectionWasActiveTarget: false);
                }

                return new TargetSwitchResult(
                    TargetSwitchOutcome.Failed,
                    $"Could not save model '{plan.SelectedModel}' for the inactive provider. Active Codex routing was not changed.",
                    target,
                    ErrorInfo.Create(ErrorCategory.Unknown, ex.Message, _clock.UtcNow),
                    ModelSelectionWasActiveTarget: false);
            }
        }

        if (plan.ActiveSwitchPlan is null)
        {
            return new TargetSwitchResult(
                TargetSwitchOutcome.Failed,
                "The active provider model switch was not prepared.",
                target,
                ErrorInfo.Create(ErrorCategory.Unknown, "Active model switch plan missing", _clock.UtcNow),
                ModelSelectionWasActiveTarget: true);
        }

        if (_runtimeModelCatalogVerifier is null)
        {
            return new TargetSwitchResult(
                TargetSwitchOutcome.Failed,
                "The active API model cannot be changed because runtime model verification is unavailable. The selected model and active route were not changed.",
                target,
                ErrorInfo.Create(ErrorCategory.Unknown, "Runtime model catalog verifier is unavailable", _clock.UtcNow),
                ModelSelectionWasActiveTarget: true);
        }

        profile.SetSelectedModel(plan.SelectedModel);
        TargetSwitchResult activeResult;
        try
        {
            activeResult = await ExecuteApiProviderSwitchAsync(
                plan.ActiveSwitchPlan with { TargetProfile = profile },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            activeResult = new TargetSwitchResult(
                TargetSwitchOutcome.Failed,
                $"Failed to apply model '{plan.SelectedModel}' to the active provider: {ex.Message}",
                target,
                ErrorInfo.Create(ErrorCategory.Unknown, ex.Message, _clock.UtcNow));
        }

        if (activeResult.Outcome is TargetSwitchOutcome.Success or TargetSwitchOutcome.SuccessWithReopenWarning or TargetSwitchOutcome.NoOp)
        {
            _audit.Record(
                "api-model-selection",
                activeResult.Outcome == TargetSwitchOutcome.Success ? "applied-active" : "applied-active-with-warning",
                $"PROFILE_ID={profile.Id:D} MODEL={plan.SelectedModel} RUNTIME_VERIFIED={activeResult.DiagnosticTrace?.ActiveModelListVerified == true}");
            return activeResult with { ModelSelectionWasActiveTarget = true };
        }

        profile.SetSelectedModel(plan.PreviousSelectedModel);
        try
        {
            _apiProviderStore.Save(profile);
            return activeResult with { ModelSelectionWasActiveTarget = true };
        }
        catch (Exception rollbackException)
        {
            var persisted = _apiProviderStore.GetById(profile.Id)?.GetEffectiveSelectedModel();
            return new TargetSwitchResult(
                TargetSwitchOutcome.Failed,
                $"{activeResult.Message} The previous selected model could not be restored; the profile currently stores '{persisted ?? "no model"}'.",
                activeResult.Target,
                ErrorInfo.Create(ErrorCategory.Unknown, rollbackException.Message, _clock.UtcNow),
                activeResult.ClosedProcesses,
                activeResult.ReopenFailures,
                activeResult.DiagnosticTrace,
                ModelSelectionWasActiveTarget: true);
        }
    }

    private TargetSwitchResult ExecuteInvalidPlan(InvalidSwitchPlan invalidPlan)
    {
        return new TargetSwitchResult(
            TargetSwitchOutcome.Failed,
            invalidPlan.ErrorMessage,
            invalidPlan.Target,
            ErrorInfo.Create(invalidPlan.Category, invalidPlan.ErrorMessage, _clock.UtcNow));
    }

    private static TargetSwitchResult ExecuteChatGptNoOp(ChatGptNoOpSwitchPlan noOpPlan)
    {
        return new TargetSwitchResult(
            TargetSwitchOutcome.Success,
            $"{noOpPlan.ActiveProfile?.DisplayName ?? "ChatGPT"} já é a conta ativa.",
            new ActiveTarget.ChatGpt(noOpPlan.ActiveProfile?.Id, noOpPlan.ActiveProfile?.AccountEmail));
    }

    private TargetSwitchResult ExecuteApiRouteSwitch(ApiRouteSwitchPlan routePlan)
    {
        var profile = routePlan.Profile;
        profile.SelectedRouteId = routePlan.RouteId;
        profile.BaseUrl = routePlan.NewBaseUrl;
        _apiProviderStore.Save(profile);

        if (routePlan.IsCurrentlyActiveInToml)
        {
            _routingConfig.UpdateProviderRoute(_paths.ConfigTomlPath, profile.StableCodexProviderId, routePlan.NewBaseUrl);
        }

        _audit.Record("route-switch", "ok", $"{profile.DisplayName} -> {routePlan.RouteId} ({routePlan.NewBaseUrl})");
        return new TargetSwitchResult(
            TargetSwitchOutcome.Success,
            $"Switched {profile.DisplayName} route to {routePlan.RouteId}.",
            new ActiveTarget.Api(profile));
    }

    private async Task<TargetSwitchResult> ExecuteApiProviderSwitchAsync(
        ApiProviderSwitchPlan apiPlan,
        CancellationToken cancellationToken)
    {
        var targetProfile = apiPlan.TargetProfile;
        var options = apiPlan.Options;
        var model = targetProfile.GetEffectiveSelectedModel();

        if (string.IsNullOrWhiteSpace(model))
        {
            return new TargetSwitchResult(
                TargetSwitchOutcome.Failed,
                "Select an enabled model before activating this API profile.",
                new ActiveTarget.Api(targetProfile),
                ErrorInfo.Create(ErrorCategory.Unknown, "No selected API model", _clock.UtcNow));
        }

        targetProfile.ModelInventory ??= new ApiProviderModelInventory();
        targetProfile.ModelInventory.EnsureSelectedModelMigrated(
            model,
            displayName: null,
            contextWindow: targetProfile.ModelOverrides?.ContextWindowTokens,
            overrides: targetProfile.ModelOverrides);
        var selectedInventoryItem = targetProfile.ModelInventory.Models.FirstOrDefault(item =>
            string.Equals(item.Slug, model, StringComparison.Ordinal));
        if (selectedInventoryItem is null || !selectedInventoryItem.Enabled)
        {
            return new TargetSwitchResult(
                TargetSwitchOutcome.Failed,
                "Select an enabled model before activating this API profile.",
                new ActiveTarget.Api(targetProfile),
                ErrorInfo.Create(ErrorCategory.Unknown, "Selected API model is not enabled", _clock.UtcNow));
        }

        var secretOwnerId = targetProfile.EndpointId ?? targetProfile.Id;
        var currentRouting = _routingConfig.ReadRoutingState(_paths.ConfigTomlPath);
        var previousTargetSummary = currentRouting.ModelProvider ?? "openai";

        var trace = new SwitchDiagnosticTrace
        {
            RequestedEndpointId = targetProfile.EndpointId,
            RequestedModelConfigId = targetProfile.Id,
            RequestedModel = model,
            PreviousTargetSummary = previousTargetSummary,
            SecretOwnerResolved = secretOwnerId,
            SwitchPlan = nameof(ApiProviderSwitchPlan),
        };

        var compensator = CreateCompensationCoordinator();
        IReadOnlyList<CodexProcessInfo> captured = [];
        IReadOnlyList<CodexProcessInfo> closed = [];

        // 1. Read and backup config.toml exact bytes
        byte[]? configOriginalBytes = _fs.FileExists(_paths.ConfigTomlPath)
            ? _fs.ReadAllBytes(_paths.ConfigTomlPath)
            : null;

        if (configOriginalBytes is not null)
        {
            compensator.RegisterConfigBackup(configOriginalBytes);
        }

        // 2. Assert auth.json pre-switch hash
        byte[]? authPreBytes = _fs.FileExists(_paths.ActiveAuthPath)
            ? _fs.ReadAllBytes(_paths.ActiveAuthPath)
            : null;
        byte[]? authPreHash = authPreBytes is not null ? SHA256.HashData(authPreBytes) : null;

        try
        {
            var transport = targetProfile.TransportOverrides;
            var toolPolicy = apiPlan.ToolPolicy ?? EffectiveToolPolicy.Resolve(
                transport?.ResponsesPolicy ?? ResponsesCompatibilityPolicy.Auto,
                DeriveRouteCapabilities(targetProfile));

            // Catalog generation INSIDE transaction try block
            string? modelCatalogJson = null;
            try
            {
                options.Progress?.Report("Updating catalog...");
                modelCatalogJson = _modelCatalogService?.EnsureProfileModelCatalog(
                    targetProfile,
                    model,
                    targetProfile.ModelOverrides?.ContextWindowTokens,
                    targetProfile.ModelOverrides,
                    toolPolicy);

                if (string.IsNullOrWhiteSpace(modelCatalogJson))
                {
                    throw new InvalidOperationException("A profile-specific model catalog is required for every active API provider with an enabled model.");
                }
            }
            catch (Exception catEx)
            {
                trace.PostconditionFailureReason = $"Model catalog generation failed: {catEx.Message}";
                _audit.Record("switch-target", "failed", $"catalog error: {catEx.Message}");
                return new TargetSwitchResult(
                    TargetSwitchOutcome.Failed,
                    $"Failed to generate model catalog for {model}: {catEx.Message}",
                    new ActiveTarget.Api(targetProfile),
                    ErrorInfo.Create(ErrorCategory.Unknown, catEx.Message, _clock.UtcNow),
                    DiagnosticTrace: trace);
            }

            var oldCatalogHash = GetFileSha256(currentRouting.ModelCatalogJson);
            var newCatalogHash = GetFileSha256(modelCatalogJson);
            var catalogChanged = !string.Equals(oldCatalogHash, newCatalogHash, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(currentRouting.ModelCatalogJson) != !string.IsNullOrWhiteSpace(modelCatalogJson));
            var closeApps = options.CloseReopenMode == CloseReopenMode.Automatic || catalogChanged;

            trace.CatalogChanged = catalogChanged;
            trace.RuntimeRestartRequired = closeApps;

            // Process discovery is global to the Windows session; it cannot tell which CODEX_HOME
            // a running process uses. Only close those processes when this transaction targets the
            // user's default .codex home. For an isolated/custom home, the runtime verifier below
            // validates that home without touching unrelated Codex applications.
            if (closeApps && AllowsGlobalProcessControl())
            {
                options.Progress?.Report("Restarting Codex...");
                captured = _processes.FindRunningCodexProcesses().Where(p => p.IsClosable).ToList();
                closed = await _processes.CloseGracefullyThenKillAsync(captured, options.GracefulCloseTimeout, cancellationToken).ConfigureAwait(false);
                compensator.RegisterCapturedProcesses(captured);

                if (_processes.AnyCodexCliRunning())
                {
                    compensator.ReopenDesktop(captured, out _);
                    _audit.Record("api-switch", "aborted", "process remnant");
                    trace.PostconditionFailureReason = "Codex CLI process is still running.";
                    return new TargetSwitchResult(
                        TargetSwitchOutcome.AbortedProcessRemnant,
                        "Switch aborted: Codex CLI process is still running. No changes made.",
                        new ActiveTarget.Api(targetProfile),
                        ErrorInfo.Create(ErrorCategory.ProcessRemnant, "Process remnant", _clock.UtcNow),
                        closed,
                        DiagnosticTrace: trace);
                }
            }

            // Standalone search requires explicit route qualification AND provider opt-in
            bool? effectiveStandaloneSearch = (toolPolicy.AllowStandaloneWebSearch && transport?.SupportsStandaloneWebSearch == true) ? true : null;

            var providerBlock = new CodexProviderBlock(
                targetProfile.StableCodexProviderId,
                targetProfile.DisplayName,
                targetProfile.BaseUrl,
                targetProfile.WireApi,
                apiPlan.BrokerPath,
                new[] { "--key-id", secretOwnerId.ToString("D") },
                5000,
                transport?.RequestMaxRetries,
                transport?.StreamMaxRetries,
                transport?.StreamIdleTimeoutMs,
                transport?.WebSocketConnectTimeoutMs,
                transport?.SupportsWebSockets,
                effectiveStandaloneSearch,
                transport?.QueryParams,
                transport?.HttpHeaders,
                transport?.EnvHttpHeaders,
                transport?.ResponsesPolicy ?? ResponsesCompatibilityPolicy.Auto,
                toolPolicy);

            _routingConfig.ApplySwitchboardRouting(_paths.ConfigTomlPath, providerBlock, model, targetProfile.ModelOverrides, modelCatalogJson);
            try
            {
                var validProviderIds = new HashSet<string>(_apiProviderStore.GetAll().Select(p => p.StableCodexProviderId), StringComparer.OrdinalIgnoreCase);
                _routingConfig.CleanOrphanProviderBlocks(_paths.ConfigTomlPath, validProviderIds);
            }
            catch
            {
                // Non-fatal hygiene pass
            }
            trace.ConfigChanged = true;

            // 4. Verify auth.json is UNTOUCHED
            if (authPreHash is not null)
            {
                var authPostBytes = _fs.ReadAllBytes(_paths.ActiveAuthPath);
                var authPostHash = SHA256.HashData(authPostBytes);
                if (!CryptographicOperations.FixedTimeEquals(authPreHash, authPostHash))
                {
                    throw new InvalidOperationException("CRITICAL: auth.json bytes modified during API provider switch.");
                }
            }

            // SECTION G: Postcondition Verification
            var postRouting = _routingConfig.ReadRoutingState(_paths.ConfigTomlPath);
            trace.EffectiveModelProvider = postRouting.ModelProvider;
            trace.EffectiveModel = postRouting.Model;
            trace.EffectiveModelCatalogJson = postRouting.ModelCatalogJson;

            // Postcondition 1: generated config contains desired model_provider
            if (!string.Equals(postRouting.ModelProvider, targetProfile.StableCodexProviderId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Postcondition failed: config model_provider was '{postRouting.ModelProvider}', expected '{targetProfile.StableCodexProviderId}'.");
            }

            // Postcondition 2: generated config contains desired model
            if (!string.Equals(postRouting.Model, model, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Postcondition failed: config model was '{postRouting.Model}', expected '{model}'.");
            }

            // Postcondition 3: required catalog is valid and persisted if requested
            if (!string.IsNullOrWhiteSpace(modelCatalogJson))
            {
                var normalizedExpected = Path.GetFullPath(modelCatalogJson).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var actualClean = postRouting.ModelCatalogJson?.Replace(@"\\", @"\");
                var normalizedActual = !string.IsNullOrWhiteSpace(actualClean)
                    ? Path.GetFullPath(actualClean).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    : null;

                if (!_fs.FileExists(modelCatalogJson) || !string.Equals(normalizedActual, normalizedExpected, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Postcondition failed: model catalog '{modelCatalogJson}' was not properly configured in config.toml (actual: '{postRouting.ModelCatalogJson}').");
                }
            }

            // Postcondition 4: Tool policy restrictions reflected in config and catalog
            var configContent = _fs.FileExists(_paths.ConfigTomlPath) ? _fs.ReadAllText(_paths.ConfigTomlPath) : string.Empty;
            if (!toolPolicy.AllowHostedWebSearch && !configContent.Contains("web_search = \"disabled\"", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Postcondition failed: web_search = \"disabled\" was not persisted in config.toml.");
            }
            if (!toolPolicy.AllowToolSearch && !string.IsNullOrWhiteSpace(modelCatalogJson) && _fs.FileExists(modelCatalogJson))
            {
                using var catalogDoc = JsonDocument.Parse(_fs.ReadAllText(modelCatalogJson));
                if (catalogDoc.RootElement.TryGetProperty("models", out var modelsArray))
                {
                    foreach (var m in modelsArray.EnumerateArray())
                    {
                        if (m.TryGetProperty("slug", out var slugProp) &&
                            string.Equals(slugProp.GetString(), model, StringComparison.OrdinalIgnoreCase))
                        {
                            if (m.TryGetProperty("supports_search_tool", out var sst) && sst.GetBoolean())
                            {
                                throw new InvalidOperationException($"Postcondition failed: supports_search_tool = true in model catalog for model '{model}' when tool_search was disallowed.");
                            }
                        }
                    }
                }
            }

            // A custom catalog is a startup-time runtime input. After closing
            // relevant Codex processes and applying config, start a short-lived
            // owned production app-server and prove model/list sees the exact
            // enabled inventory before reporting routing success or reopening
            // Desktop. The verifier owns only its own process.
            if (!string.IsNullOrWhiteSpace(modelCatalogJson) && _runtimeModelCatalogVerifier is not null)
            {
                options.Progress?.Report("Verifying models...");
                var expectedModels = targetProfile.ModelInventory.GetEnabledModels()
                    .Select(item => item.Slug)
                    .ToArray();
                var runtimeVerification = await _runtimeModelCatalogVerifier.VerifyAsync(
                    targetProfile.StableCodexProviderId,
                    model,
                    expectedModels,
                    cancellationToken).ConfigureAwait(false);
                if (!runtimeVerification.Succeeded)
                {
                    throw new InvalidOperationException($"Runtime model/list verification failed: {runtimeVerification.FailureReason ?? "unknown failure"}");
                }

                trace.ActiveModelListVerified = true;
                // The owned app-server was started after the catalog changed;
                // even if Desktop was not previously running, this proves the
                // startup-only catalog was loaded by the exact runtime.
                trace.RuntimeRestartCompleted = true;
            }

            // 5. Update profile metadata
            targetProfile.LastSwitchedAt = _clock.UtcNow;
            _apiProviderStore.Save(targetProfile);
            trace.RoutingStateCommitted = true;
            trace.FinalTargetMatchesRequested = true;
            _audit.Record("switch-target", "ok", $"-> API {targetProfile.DisplayName} [{trace.ToSanitizedSummary().Replace('\n', ' ').Replace('\r', ' ')}]");

            // 6. Reopen Codex if captured
            var reopenFailures = new List<CodexProcessInfo>();
            if (closeApps && options.ReopenDesktopAfterSwitch)
            {
                compensator.ReopenDesktop(captured, out reopenFailures);
                trace.RuntimeRestartCompleted = reopenFailures.Count == 0;
            }

            var activeTarget = new ActiveTarget.Api(targetProfile);
            if (reopenFailures.Count > 0)
            {
                return new TargetSwitchResult(
                    TargetSwitchOutcome.SuccessWithReopenWarning,
                    $"Switched routing to {targetProfile.DisplayName}, but some apps failed to reopen.",
                    activeTarget, null, closed, reopenFailures,
                    DiagnosticTrace: trace);
            }

            return new TargetSwitchResult(
                TargetSwitchOutcome.Success,
                $"Active inference route switched to {targetProfile.DisplayName}.",
                activeTarget, null, closed,
                DiagnosticTrace: trace);
        }
        catch (Exception ex)
        {
            trace.PostconditionFailureReason = ex.Message;
            trace.FinalTargetMatchesRequested = false;
            await compensator.CompensateAsync("switch-target", ex.Message, cancellationToken).ConfigureAwait(false);
            return new TargetSwitchResult(
                TargetSwitchOutcome.RolledBack,
                $"Failed to switch to API provider. Original configuration was restored: {ex.Message}",
                new ActiveTarget.Api(targetProfile),
                ErrorInfo.Create(ErrorCategory.Unknown, ex.Message, _clock.UtcNow),
                DiagnosticTrace: trace);
        }
    }

    private async Task<TargetSwitchResult> ExecuteChatGptAccountSwitchAsync(
        ChatGptAccountSwitchPlan accountPlan,
        CancellationToken cancellationToken)
    {
        var targetProfile = accountPlan.TargetProfile;
        var options = accountPlan.Options;
        var isApiRoutingActive = accountPlan.IsApiRoutingActive;

        var compensator = CreateCompensationCoordinator();
        byte[]? configOriginalBytes = _fs.FileExists(_paths.ConfigTomlPath)
            ? _fs.ReadAllBytes(_paths.ConfigTomlPath)
            : null;

        if (configOriginalBytes is not null && isApiRoutingActive)
        {
            compensator.RegisterConfigBackup(configOriginalBytes);
        }

        var initialRouting = _routingConfig.ReadRoutingState(_paths.ConfigTomlPath);
        var catalogChanged = isApiRoutingActive && !string.IsNullOrWhiteSpace(initialRouting.ModelCatalogJson);

        // If API provider routing was active, reset config.toml to openai BEFORE SwitchService switches credentials
        // so that when SwitchService relaunches desktop apps, config.toml already points to openai.
        if (isApiRoutingActive)
        {
            try
            {
                _routingConfig.ReturnToOpenAi(_paths.ConfigTomlPath);
            }
            catch (Exception ex)
            {
                return new TargetSwitchResult(
                    TargetSwitchOutcome.Failed,
                    $"Failed to reset model_provider to openai: {ex.Message}",
                    new ActiveTarget.ChatGpt(targetProfile.Id, targetProfile.AccountEmail),
                    ErrorInfo.Create(ErrorCategory.Unknown, ex.Message, _clock.UtcNow));
            }
        }

        // Execute the full ChatGPT account switch transaction using SwitchService
        var switchOptions = (options.CloseReopenMode != CloseReopenMode.Automatic && catalogChanged)
            ? options with { CloseReopenMode = CloseReopenMode.Automatic }
            : options;

        var chatGptResult = await _chatGptSwitchService.SwitchAsync(
            targetProfile.Id,
            switchOptions,
            cancellationToken).ConfigureAwait(false);

        if (chatGptResult.Outcome != SwitchOutcome.Success && chatGptResult.Outcome != SwitchOutcome.SuccessWithReopenWarning)
        {
            // If switch failed or rolled back, restore config.toml if it was previously in API mode
            if (isApiRoutingActive && configOriginalBytes is not null)
            {
                _routingConfig.RestoreExactBytes(_paths.ConfigTomlPath, configOriginalBytes);
            }

            var targetOutcome = chatGptResult.Outcome switch
            {
                SwitchOutcome.RolledBack => TargetSwitchOutcome.RolledBack,
                SwitchOutcome.AbortedProcessRemnant => TargetSwitchOutcome.AbortedProcessRemnant,
                _ => TargetSwitchOutcome.Failed,
            };

            return new TargetSwitchResult(
                targetOutcome,
                chatGptResult.Message,
                new ActiveTarget.ChatGpt(targetProfile.Id, targetProfile.AccountEmail),
                chatGptResult.Error,
                chatGptResult.ClosedProcesses,
                chatGptResult.ReopenFailures);
        }

        // Guarantee config.toml has model_provider = openai (idempotent, ensures consistency)
        try
        {
            _routingConfig.ReturnToOpenAi(_paths.ConfigTomlPath);
            var validProviderIds = new HashSet<string>(_apiProviderStore.GetAll().Select(p => p.StableCodexProviderId), StringComparer.OrdinalIgnoreCase);
            _routingConfig.CleanOrphanProviderBlocks(_paths.ConfigTomlPath, validProviderIds);
        }
        catch
        {
            // Non-fatal if config is already openai
        }

        _audit.Record("switch-target", "ok", $"-> ChatGPT {targetProfile.DisplayName}");

        return new TargetSwitchResult(
            chatGptResult.Outcome == SwitchOutcome.SuccessWithReopenWarning
                ? TargetSwitchOutcome.SuccessWithReopenWarning
                : TargetSwitchOutcome.Success,
            $"Active inference target switched to ChatGPT account {targetProfile.DisplayName}.",
            new ActiveTarget.ChatGpt(targetProfile.Id, targetProfile.AccountEmail),
            null,
            chatGptResult.ClosedProcesses,
            chatGptResult.ReopenFailures);
    }

    private async Task<TargetSwitchResult> ExecuteChatGptReturnRoutingAsync(
        ChatGptReturnRoutingSwitchPlan returnPlan,
        CancellationToken cancellationToken)
    {
        var targetProfile = returnPlan.ActiveProfile;
        var options = returnPlan.Options;
        var currentRouting = _routingConfig.ReadRoutingState(_paths.ConfigTomlPath);
        var catalogChanged = !string.IsNullOrWhiteSpace(currentRouting.ModelCatalogJson);
        // Process discovery is global to the Windows session, so a routing-only
        // return from an isolated/custom CODEX_HOME must never close unrelated Codex apps.
        var closeApps = (options.CloseReopenMode == CloseReopenMode.Automatic || catalogChanged) && AllowsGlobalProcessControl();

        var compensator = CreateCompensationCoordinator();
        IReadOnlyList<CodexProcessInfo> captured = [];
        IReadOnlyList<CodexProcessInfo> closed = [];

        if (closeApps)
        {
            captured = _processes.FindRunningCodexProcesses().Where(p => p.IsClosable).ToList();
            closed = await _processes.CloseGracefullyThenKillAsync(captured, options.GracefulCloseTimeout, cancellationToken).ConfigureAwait(false);
            compensator.RegisterCapturedProcesses(captured);

            if (_processes.AnyCodexCliRunning())
            {
                compensator.ReopenDesktop(captured, out _);
                _audit.Record("switch", "aborted", "process remnant");
                return new TargetSwitchResult(
                    TargetSwitchOutcome.AbortedProcessRemnant,
                    "A troca foi abortada: ainda há um processo do Codex em execução. Nada foi alterado.",
                    new ActiveTarget.ChatGpt(targetProfile?.Id, targetProfile?.AccountEmail),
                    ErrorInfo.Create(ErrorCategory.ProcessRemnant, "Process remnant", _clock.UtcNow),
                    closed);
            }
        }

        byte[]? configOriginalBytes = _fs.FileExists(_paths.ConfigTomlPath)
            ? _fs.ReadAllBytes(_paths.ConfigTomlPath)
            : null;

        if (configOriginalBytes is not null)
        {
            compensator.RegisterConfigBackup(configOriginalBytes);
        }

        try
        {
            _routingConfig.ReturnToOpenAi(_paths.ConfigTomlPath);
            var validProviderIds = new HashSet<string>(_apiProviderStore.GetAll().Select(p => p.StableCodexProviderId), StringComparer.OrdinalIgnoreCase);
            _routingConfig.CleanOrphanProviderBlocks(_paths.ConfigTomlPath, validProviderIds);
        }
        catch (Exception ex)
        {
            await compensator.CompensateAsync("switch-target", ex.Message, cancellationToken).ConfigureAwait(false);
            return new TargetSwitchResult(
                TargetSwitchOutcome.RolledBack,
                $"Failed to return model_provider to openai: {ex.Message}",
                new ActiveTarget.ChatGpt(targetProfile?.Id, targetProfile?.AccountEmail),
                ErrorInfo.Create(ErrorCategory.Unknown, ex.Message, _clock.UtcNow));
        }

        var reopenFailures = new List<CodexProcessInfo>();
        if (closeApps && options.ReopenDesktopAfterSwitch)
        {
            compensator.ReopenDesktop(captured, out reopenFailures);
        }

        _audit.Record("switch-target", "ok", "-> ChatGPT (routing returned)");

        return new TargetSwitchResult(
            reopenFailures.Count > 0 ? TargetSwitchOutcome.SuccessWithReopenWarning : TargetSwitchOutcome.Success,
            "Active inference route returned to ChatGPT.",
            new ActiveTarget.ChatGpt(targetProfile?.Id, targetProfile?.AccountEmail),
            null,
            closed,
            reopenFailures);
    }

    private string? GetFileSha256(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !_fs.FileExists(path)) return null;
        try
        {
            return Convert.ToHexString(SHA256.HashData(_fs.ReadAllBytes(path)));
        }
        catch
        {
            return null;
        }
    }

    private static RouteCapabilities DeriveRouteCapabilities(ApiProviderProfile targetProfile)
    {
        if (targetProfile.LastProbeReport is { } report)
        {
            return new RouteCapabilities(
                routeId: targetProfile.SelectedRouteId ?? "active",
                baseUrl: targetProfile.BaseUrl,
                responses: report.ResponsesEndpoint,
                streaming: report.StreamingSupport,
                webSockets: CapabilityEvidence.Unknown("WebSocket route not probed"),
                hostedWebSearch: report.HostedSearchSupport,
                standardFunctionTools: report.BuiltInFunctionTools,
                visionPassthrough: report.Vision,
                customFreeformTools: report.CustomApplyPatch,
                applyPatchFreeform: report.CustomApplyPatch,
                toolSearch: report.ToolSearch,
                standaloneWebSearch: report.StandaloneSearch,
                namespaceTools: report.McpNamespaceTools,
                promptCaching: CapabilityEvidence.Unknown("Prompt caching unverified"),
                mcp: CapabilityEvidence.Unknown("MCP unverified"),
                appsPlugins: report.Plugins);
        }

        if (targetProfile.BaseUrl.Contains("modelflare", StringComparison.OrdinalIgnoreCase))
        {
            return RouteCapabilities.ForModelflareGrok46(targetProfile.BaseUrl);
        }

        return RouteCapabilities.ForGenericResponses(targetProfile.BaseUrl);
    }
}
