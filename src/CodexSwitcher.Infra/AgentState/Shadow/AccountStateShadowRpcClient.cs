using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexSwitcher.Core.AgentState.V1;

namespace CodexSwitcher.Infra.AgentState.Shadow;

internal sealed class AccountStateShadowRpcClient : IAccountStateShadowClient
{
    internal const int MaximumFrameBytes = 1_048_576;
    internal const string ProtocolManifestSha256 = "72602ae175fd1261f870e2815065da6d96fdf4bd81fce93419e4f031916e90ad";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { MaxDepth = 32 };
    private static readonly byte[] NewLine = [(byte)'\n'];

    private readonly NamedPipeClientStream _pipe;
    private int _requestId;

    public bool SupportsNicknameMutation { get; private set; }
    public bool SupportsNicknameAuthority { get; private set; }

    public AccountStateShadowRpcClient(string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }

    public Task ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        _pipe.ConnectAsync(checked((int)Math.Clamp(timeout.TotalMilliseconds, 1, int.MaxValue)), cancellationToken);

    public async Task VerifyCompatibleAsync(CancellationToken cancellationToken)
    {
        try
        {
            var ping = await CallAsync("system.ping", new { }, cancellationToken).ConfigureAwait(false);
            if (!ping.TryGetProperty("alive", out var alive) || alive.ValueKind != JsonValueKind.True)
                throw new AccountStateShadowCompatibilityException();

            var info = await CallAsync("system.info", new { }, cancellationToken).ConfigureAwait(false);
            if (ReadInt32(info, "protocolSchemaVersion") != 1 ||
                !string.Equals(ReadString(info, "protocolManifestSha256"), ProtocolManifestSha256, StringComparison.Ordinal) ||
                !info.TryGetProperty("capabilities", out var capabilities) || capabilities.ValueKind != JsonValueKind.Array)
            {
                throw new AccountStateShadowCompatibilityException();
            }

            var capability = capabilities.EnumerateArray().FirstOrDefault(item =>
                item.ValueKind == JsonValueKind.Object &&
                item.TryGetProperty("capabilityId", out var capabilityId) &&
                capabilityId.ValueKind == JsonValueKind.String &&
                capabilityId.GetString() == "shadow.accounts.reconcile");
            if (capability.ValueKind != JsonValueKind.Object ||
                !capability.TryGetProperty("supported", out var supported) || supported.ValueKind != JsonValueKind.True ||
                !capability.TryGetProperty("schemaVersion", out var capabilityVersion) ||
                capabilityVersion.ValueKind != JsonValueKind.Number || !capabilityVersion.TryGetInt32(out var version) || version != 1 ||
                !capability.TryGetProperty("extensions", out var extensions) || extensions.ValueKind != JsonValueKind.Object ||
                !extensions.TryGetProperty("protocolSchemaVersion", out var extensionVersion) ||
                extensionVersion.ValueKind != JsonValueKind.Number || !extensionVersion.TryGetInt32(out version) || version != 1 ||
                !extensions.TryGetProperty("protocolManifestSha256", out var extensionHash) ||
                extensionHash.ValueKind != JsonValueKind.String || extensionHash.GetString() != ProtocolManifestSha256)
            {
                throw new AccountStateShadowCompatibilityException();
            }

            SupportsNicknameMutation = capabilities.EnumerateArray().Any(item =>
                item.ValueKind == JsonValueKind.Object &&
                item.TryGetProperty("capabilityId", out var capabilityId) &&
                capabilityId.ValueKind == JsonValueKind.String &&
                capabilityId.GetString() == "accounts.updateMetadata" &&
                item.TryGetProperty("supported", out var mutationSupported) &&
                mutationSupported.ValueKind == JsonValueKind.True &&
                item.TryGetProperty("schemaVersion", out var mutationVersion) &&
                mutationVersion.ValueKind == JsonValueKind.Number && mutationVersion.TryGetInt32(out var parsedVersion) && parsedVersion == 1 &&
                item.TryGetProperty("extensions", out var mutationExtensions) && mutationExtensions.ValueKind == JsonValueKind.Object &&
                mutationExtensions.TryGetProperty("protocolSchemaVersion", out var mutationProtocolVersion) &&
                mutationProtocolVersion.ValueKind == JsonValueKind.Number && mutationProtocolVersion.TryGetInt32(out parsedVersion) && parsedVersion == 1 &&
                mutationExtensions.TryGetProperty("protocolManifestSha256", out var mutationHash) &&
                mutationHash.ValueKind == JsonValueKind.String && mutationHash.GetString() == ProtocolManifestSha256);
        }
        catch (AccountStateShadowCompatibilityException)
        {
            throw;
        }
        catch (Exception exception) when (exception is AccountStateShadowRpcException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new AccountStateShadowCompatibilityException();
        }
    }

    public async Task VerifyNicknameAuthorityCompatibleAsync(CancellationToken cancellationToken)
    {
        try
        {
            var ping = await CallAsync("system.ping", new { }, cancellationToken).ConfigureAwait(false);
            if (!HasExactProperties(ping, "alive") || !ReadBoolean(ping, "alive"))
                throw new AccountStateShadowCompatibilityException();

            var info = await CallAsync("system.info", new { }, cancellationToken).ConfigureAwait(false);
            if (!HasExactProperties(info, "daemonVersion", "protocolSchemaVersion", "protocolManifestSha256", "databaseSchemaVersion", "startedAt", "uptimeSeconds", "capabilities") ||
                ReadInt32(info, "protocolSchemaVersion") != 1 ||
                !string.Equals(ReadString(info, "protocolManifestSha256"), ProtocolManifestSha256, StringComparison.Ordinal) ||
                ReadInt32(info, "databaseSchemaVersion") is not (1 or 2) ||
                !info.TryGetProperty("capabilities", out var capabilities) || capabilities.ValueKind != JsonValueKind.Array || capabilities.GetArrayLength() != 8)
            {
                throw new AccountStateShadowCompatibilityException();
            }

            var capabilityMap = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var capability in capabilities.EnumerateArray())
            {
                if (!HasExactProperties(capability, "schemaVersion", "capabilityId", "supported", "extensions") ||
                    ReadInt32(capability, "schemaVersion") != 1)
                    throw new AccountStateShadowCompatibilityException();

                var id = ReadString(capability, "capabilityId");
                if (!capabilityMap.TryAdd(id, capability))
                    throw new AccountStateShadowCompatibilityException();
            }

            string[] expectedIds =
            [
                "shadow.accounts.reconcile",
                "accounts.updateMetadata",
                "accounts.getNickname",
                "accounts.listNicknames",
                "accounts.updateNickname",
                "shadow.accounts.reconcileSource",
                "accounts.nicknameAuthority.get",
                "accounts.nicknameAuthority.lifecycle"
            ];
            if (expectedIds.Any(id => !capabilityMap.ContainsKey(id)))
                throw new AccountStateShadowCompatibilityException();

            var parsed = new Dictionary<string, bool>(StringComparer.Ordinal);
            string? authorityState = null;
            bool? experimentalEnabled = null;
            foreach (var id in expectedIds)
            {
                var capability = capabilityMap[id];
                var expectedSupported = id != "shadow.accounts.reconcile";
                if (ReadBoolean(capability, "supported") != expectedSupported ||
                    !capability.TryGetProperty("extensions", out var extensions) ||
                    !HasExactProperties(extensions, "protocolSchemaVersion", "protocolManifestSha256", "enabled", "authorityState", "experimentalNicknameAuthorityEnabled") ||
                    ReadInt32(extensions, "protocolSchemaVersion") != 1 ||
                    !string.Equals(ReadString(extensions, "protocolManifestSha256"), ProtocolManifestSha256, StringComparison.Ordinal))
                {
                    throw new AccountStateShadowCompatibilityException();
                }

                var state = ReadString(extensions, "authorityState");
                if (!NicknameAuthorityStates.IsKnown(state))
                    throw new AccountStateShadowCompatibilityException();
                if (authorityState is not null && !string.Equals(authorityState, state, StringComparison.Ordinal))
                    throw new AccountStateShadowCompatibilityException();
                authorityState = state;

                var experimental = ReadBoolean(extensions, "experimentalNicknameAuthorityEnabled");
                if (experimentalEnabled.HasValue && experimentalEnabled.Value != experimental)
                    throw new AccountStateShadowCompatibilityException();
                experimentalEnabled = experimental;
                parsed.Add(id, ReadBoolean(extensions, "enabled"));
            }

            var databaseVersion = ReadInt32(info, "databaseSchemaVersion");
            var expectedEnabled = new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                ["shadow.accounts.reconcile"] = false,
                ["accounts.updateMetadata"] = authorityState == NicknameAuthorityStates.LegacyLocal,
                ["accounts.getNickname"] = true,
                ["accounts.listNicknames"] = true,
                ["accounts.updateNickname"] = authorityState == NicknameAuthorityStates.DaemonNicknameAuthority ||
                    (authorityState == NicknameAuthorityStates.Preparing && experimentalEnabled == true),
                ["shadow.accounts.reconcileSource"] = authorityState == NicknameAuthorityStates.DaemonNicknameAuthority ||
                    (authorityState == NicknameAuthorityStates.Preparing && experimentalEnabled == true),
                ["accounts.nicknameAuthority.get"] = true,
                ["accounts.nicknameAuthority.lifecycle"] = experimentalEnabled == true &&
                    (authorityState != NicknameAuthorityStates.LegacyLocal || databaseVersion == 1)
            };
            if (expectedIds.Any(id => parsed[id] != expectedEnabled[id]))
                throw new AccountStateShadowCompatibilityException();

            SupportsNicknameAuthority = true;
        }
        catch (AccountStateShadowCompatibilityException)
        {
            throw;
        }
        catch (Exception exception) when (exception is AccountStateShadowRpcException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new AccountStateShadowCompatibilityException();
        }
    }

    public async Task<AccountStateShadowReconcileCounts> ReconcileAsync(AccountCollection collection, CancellationToken cancellationToken)
    {
        var result = await CallAsync("shadow.accounts.reconcile", new ReconcileParameters(collection), cancellationToken)
            .ConfigureAwait(false);
        if (ReadInt32(result, "schemaVersion") != 1)
            throw new AccountStateShadowCompatibilityException();

        var counts = new AccountStateShadowReconcileCounts(
            ReadInt32(result, "inserted"),
            ReadInt32(result, "updated"),
            ReadInt32(result, "deleted"),
            ReadInt32(result, "unchanged"));
        if (counts.Inserted < 0 || counts.Updated < 0 || counts.Deleted < 0 || counts.Unchanged < 0)
            throw new AccountStateShadowCompatibilityException();
        return counts;
    }

    public async Task<IReadOnlyList<AccountDescriptor>> ListAccountsAsync(CancellationToken cancellationToken)
    {
        var result = await CallAsync("accounts.list", new { }, cancellationToken).ConfigureAwait(false);
        if (!result.TryGetProperty("collection", out var collection) || collection.ValueKind != JsonValueKind.Object ||
            !collection.TryGetProperty("schemaVersion", out var schemaVersion) ||
            schemaVersion.ValueKind != JsonValueKind.Number || !schemaVersion.TryGetInt32(out var version) || version != 1 ||
            !collection.TryGetProperty("accounts", out var accounts) || accounts.ValueKind != JsonValueKind.Array ||
            accounts.GetArrayLength() > 1_024)
        {
            throw new AccountStateShadowCompatibilityException();
        }

        var projected = new List<AccountDescriptor>(accounts.GetArrayLength());
        foreach (var account in accounts.EnumerateArray())
            projected.Add(ParseAccountDescriptor(account));

        return projected;
    }

    public async Task<RevisionedAccountDescriptor> GetAccountAsync(string accountId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        var result = await CallAsync("accounts.get", new { accountId }, cancellationToken).ConfigureAwait(false);
        return ParseRevisionedAccount(result);
    }

    public async Task<RevisionedAccountDescriptor> UpdateMetadataAsync(AccountMetadataUpdate update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        var result = await CallAsync("accounts.updateMetadata", update, cancellationToken).ConfigureAwait(false);
        return ParseRevisionedAccount(result);
    }

    public async Task<AuthorityDaemonInfo> GetAuthorityDaemonInfoAsync(CancellationToken cancellationToken)
    {
        var info = await CallAsync("system.info", new { }, cancellationToken).ConfigureAwait(false);
        if (!HasExactProperties(info, "daemonVersion", "protocolSchemaVersion", "protocolManifestSha256", "databaseSchemaVersion", "startedAt", "uptimeSeconds", "capabilities") ||
            ReadInt32(info, "protocolSchemaVersion") != 1 ||
            ReadString(info, "protocolManifestSha256") != ProtocolManifestSha256 ||
            !info.TryGetProperty("capabilities", out var capabilities) || capabilities.ValueKind != JsonValueKind.Array)
            throw new AccountStateShadowCompatibilityException();

        var enabled = new Dictionary<string, bool>(StringComparer.Ordinal);
        string? state = null;
        bool? experimental = null;
        foreach (var capability in capabilities.EnumerateArray())
        {
            if (!HasExactProperties(capability, "schemaVersion", "capabilityId", "supported", "extensions") ||
                ReadInt32(capability, "schemaVersion") != 1)
                throw new AccountStateShadowCompatibilityException();

            var capabilityId = ReadString(capability, "capabilityId");
            var expectedSupported = capabilityId != "shadow.accounts.reconcile";
            if (ReadBoolean(capability, "supported") != expectedSupported ||
                !capability.TryGetProperty("extensions", out var extensions) ||
                !HasExactProperties(extensions, "protocolSchemaVersion", "protocolManifestSha256", "enabled", "authorityState", "experimentalNicknameAuthorityEnabled") ||
                ReadInt32(extensions, "protocolSchemaVersion") != 1 ||
                ReadString(extensions, "protocolManifestSha256") != ProtocolManifestSha256)
                throw new AccountStateShadowCompatibilityException();

            var candidateState = ReadString(extensions, "authorityState");
            if (!NicknameAuthorityStates.IsKnown(candidateState) || (state is not null && state != candidateState))
                throw new AccountStateShadowCompatibilityException();
            state = candidateState;
            var candidateExperimental = ReadBoolean(extensions, "experimentalNicknameAuthorityEnabled");
            if (experimental.HasValue && experimental.Value != candidateExperimental)
                throw new AccountStateShadowCompatibilityException();
            experimental = candidateExperimental;
            if (!enabled.TryAdd(capabilityId, ReadBoolean(extensions, "enabled")))
                throw new AccountStateShadowCompatibilityException();
        }

        return new AuthorityDaemonInfo(ReadInt32(info, "databaseSchemaVersion"), state!, experimental == true, enabled);
    }

    public async Task<NicknameAuthorityStatus> GetNicknameAuthorityAsync(CancellationToken cancellationToken)
    {
        var result = await CallAsync("accounts.nicknameAuthority.get", new { }, cancellationToken).ConfigureAwait(false);
        return ParseAuthorityStatus(result);
    }

    public async Task<NicknameAuthorityStatus> BeginNicknameAuthorityPreparationAsync(long expectedEpoch, CancellationToken cancellationToken) =>
        ParseAuthorityStatus(await CallAsync("accounts.nicknameAuthority.beginPreparation", new AuthorityEpochParameters(expectedEpoch), cancellationToken).ConfigureAwait(false));

    public async Task<NicknameAuthorityStatus> MarkNicknameAuthorityVerifiedAsync(long expectedEpoch, string migrationId, CancellationToken cancellationToken) =>
        ParseAuthorityStatus(await CallAsync("accounts.nicknameAuthority.markVerified", new AuthorityLifecycleParameters(expectedEpoch, migrationId), cancellationToken).ConfigureAwait(false));

    public async Task<NicknameAuthorityStatus> ActivateNicknameAuthorityAsync(long expectedEpoch, string migrationId, CancellationToken cancellationToken) =>
        ParseAuthorityStatus(await CallAsync("accounts.nicknameAuthority.activate", new AuthorityLifecycleParameters(expectedEpoch, migrationId), cancellationToken).ConfigureAwait(false));

    public async Task<NicknameAuthorityStatus> BeginNicknameAuthorityRollbackAsync(long expectedEpoch, string migrationId, CancellationToken cancellationToken) =>
        ParseAuthorityStatus(await CallAsync("accounts.nicknameAuthority.beginRollback", new AuthorityLifecycleParameters(expectedEpoch, migrationId), cancellationToken).ConfigureAwait(false));

    public async Task<NicknameAuthorityStatus> CompleteNicknameAuthorityRollbackAsync(long expectedEpoch, string migrationId, CancellationToken cancellationToken) =>
        ParseAuthorityStatus(await CallAsync("accounts.nicknameAuthority.completeRollback", new AuthorityLifecycleParameters(expectedEpoch, migrationId), cancellationToken).ConfigureAwait(false));

    public async Task<AccountNicknameCollection> ListNicknamesAsync(CancellationToken cancellationToken)
    {
        var result = await CallAsync("accounts.listNicknames", new { }, cancellationToken).ConfigureAwait(false);
        if (!HasExactProperties(result, "schemaVersion", "accounts") || ReadInt32(result, "schemaVersion") != 1 ||
            !result.TryGetProperty("accounts", out var accounts) || accounts.ValueKind != JsonValueKind.Array || accounts.GetArrayLength() > 1_024)
            throw new AccountStateShadowCompatibilityException();

        var rows = accounts.EnumerateArray().Select(ParseNicknameState).ToArray();
        if (rows.Select(row => row.AccountId).Distinct(StringComparer.Ordinal).Count() != rows.Length)
            throw new AccountStateShadowCompatibilityException();
        return new AccountNicknameCollection { Accounts = rows };
    }

    public async Task<AccountNicknameState> GetNicknameAsync(string accountId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        return ParseNicknameState(await CallAsync("accounts.getNickname", new AccountIdParameters(accountId), cancellationToken).ConfigureAwait(false));
    }

    public async Task<NicknameMutationResult> UpdateNicknameAsync(
        AccountNicknameMutation mutation,
        long expectedAuthorityEpoch,
        string? migrationId,
        CancellationToken cancellationToken)
    {
        ValidateMutation(mutation);
        try
        {
            var result = await CallAsync("accounts.updateNickname", new NicknameMutationParameters(mutation, expectedAuthorityEpoch, migrationId), cancellationToken).ConfigureAwait(false);
            if (!HasExactProperties(result, "nicknameState", "account") ||
                !result.TryGetProperty("nicknameState", out var state) || !result.TryGetProperty("account", out var account))
                throw new AccountStateShadowRpcOutcomeUnknownException();
            return new NicknameMutationResult { NicknameState = ParseNicknameState(state), Account = ParseAccountDescriptor(account) };
        }
        catch (AccountStateShadowRpcOutcomeUnknownException)
        {
            throw;
        }
        catch (Exception exception) when (exception is AccountStateShadowCompatibilityException or JsonException or InvalidOperationException)
        {
            // The daemon may have committed before returning an unusable response.
            throw new AccountStateShadowRpcOutcomeUnknownException();
        }
    }

    public async Task<SourceReconcileResult> ReconcileSourceAsync(
        AccountSourceCollection collection,
        long expectedAuthorityEpoch,
        string? migrationId,
        CancellationToken cancellationToken)
    {
        ValidateSourceCollection(collection);
        var result = await CallAsync("shadow.accounts.reconcileSource", new SourceReconcileParameters(collection, expectedAuthorityEpoch, migrationId), cancellationToken).ConfigureAwait(false);
        if (!HasExactProperties(result, "schemaVersion", "inserted", "updated", "deleted", "unchanged") || ReadInt32(result, "schemaVersion") != 1)
            throw new AccountStateShadowCompatibilityException();
        var counts = new SourceReconcileResult
        {
            Inserted = ReadInt32(result, "inserted"),
            Updated = ReadInt32(result, "updated"),
            Deleted = ReadInt32(result, "deleted"),
            Unchanged = ReadInt32(result, "unchanged")
        };
        if (counts.Inserted < 0 || counts.Updated < 0 || counts.Deleted < 0 || counts.Unchanged < 0)
            throw new AccountStateShadowCompatibilityException();
        return counts;
    }

    public async ValueTask DisposeAsync() => await _pipe.DisposeAsync().ConfigureAwait(false);

    private async Task<JsonElement> CallAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        if (!_pipe.IsConnected)
        {
            if (method == "accounts.updateNickname")
                throw new AccountStateShadowRpcRequestNotSentException();
            throw new IOException("The shadow state connection is unavailable.");
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(5));
        var requestToken = timeoutSource.Token;
        var requestId = Interlocked.Increment(ref _requestId);
        var request = JsonSerializer.SerializeToUtf8Bytes(new RpcRequest("2.0", requestId, method, parameters), JsonOptions);
        if (request.Length > MaximumFrameBytes)
            throw new InvalidDataException("The shadow state request exceeds the supported frame size.");

        byte[] responseBytes;
        var requestMayHaveBeenSent = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            requestMayHaveBeenSent = true;
            await _pipe.WriteAsync(request, requestToken).ConfigureAwait(false);
            await _pipe.WriteAsync(NewLine, requestToken).ConfigureAwait(false);
            await _pipe.FlushAsync(requestToken).ConfigureAwait(false);
            responseBytes = await ReadFrameAsync(requestToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (requestMayHaveBeenSent && method == "accounts.updateNickname")
        {
            throw new AccountStateShadowRpcOutcomeUnknownException();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AccountStateShadowRpcTimeoutException();
        }
        catch (IOException) when (requestMayHaveBeenSent && method == "accounts.updateNickname")
        {
            throw new AccountStateShadowRpcOutcomeUnknownException();
        }

        using var response = JsonDocument.Parse(responseBytes, new JsonDocumentOptions { MaxDepth = 32 });
        var root = response.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("jsonrpc", out var version) || version.ValueKind != JsonValueKind.String || version.GetString() != "2.0" ||
            !root.TryGetProperty("id", out var responseId) || responseId.ValueKind != JsonValueKind.Number ||
            !responseId.TryGetInt32(out var actualId) || actualId != requestId)
        {
            throw new AccountStateShadowCompatibilityException();
        }

        if (root.TryGetProperty("error", out var error))
        {
            var code = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var errorCode) &&
                errorCode.ValueKind == JsonValueKind.Number && errorCode.TryGetInt32(out var parsedCode) ? parsedCode : 0;
            var category = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("data", out var errorData) &&
                errorData.ValueKind == JsonValueKind.Object && errorData.TryGetProperty("errorCode", out var errorCategory) &&
                errorCategory.ValueKind == JsonValueKind.String
                    ? errorCategory.GetString()
                    : null;
            var retryable = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("data", out errorData) &&
                errorData.ValueKind == JsonValueKind.Object && errorData.TryGetProperty("retryable", out var retryableElement) &&
                retryableElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? retryableElement.GetBoolean()
                    : (bool?)null;
            var sanitizedCategory = AccountStateShadowRpcException.SanitizeCategory(category);
            throw new AccountStateShadowRpcException(code, sanitizedCategory, retryable);
        }
        if (!root.TryGetProperty("result", out var result))
            throw new AccountStateShadowCompatibilityException();
        return result.Clone();
    }

    private async Task<byte[]> ReadFrameAsync(CancellationToken cancellationToken)
    {
        using var frame = new MemoryStream(capacity: 4096);
        var buffer = new byte[4096];
        while (true)
        {
            var read = await _pipe.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException("The shadow state connection closed before responding.");

            var newline = Array.IndexOf(buffer, (byte)'\n', 0, read);
            var length = newline >= 0 ? newline : read;
            if (frame.Length + length > MaximumFrameBytes)
                throw new InvalidDataException("The shadow state response exceeds the supported frame size.");
            frame.Write(buffer, 0, length);
            if (newline >= 0)
            {
                var bytes = frame.ToArray();
                if (bytes.Length > 0 && bytes[^1] == (byte)'\r')
                    Array.Resize(ref bytes, bytes.Length - 1);
                return bytes;
            }
        }
    }

    private static int ReadInt32(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value)
            ? value
            : throw new AccountStateShadowCompatibilityException();

    private static long ReadInt64(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var value)
            ? value
            : throw new AccountStateShadowCompatibilityException();

    private static bool ReadBoolean(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : throw new AccountStateShadowCompatibilityException();

    private static string ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()!
            : throw new AccountStateShadowCompatibilityException();

    private static RevisionedAccountDescriptor ParseRevisionedAccount(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("account", out var accountElement) ||
            !result.TryGetProperty("revision", out var revisionElement) || revisionElement.ValueKind != JsonValueKind.String)
        {
            throw new AccountStateShadowCompatibilityException();
        }

        var revision = revisionElement.GetString();
        if (string.IsNullOrEmpty(revision) || revision.Length > 256)
            throw new AccountStateShadowCompatibilityException();

        return new RevisionedAccountDescriptor(ParseAccountDescriptor(accountElement), revision);
    }

    private static AccountDescriptor ParseAccountDescriptor(JsonElement account)
    {
        if (!HasExactProperties(account, "schemaVersion", "accountId", "displayName", "health", "isActive", "sortOrder", "createdAt", "planType") ||
            !account.TryGetProperty("schemaVersion", out var itemVersion) ||
            itemVersion.ValueKind != JsonValueKind.Number || !itemVersion.TryGetInt32(out var version) || version != 1 ||
            !account.TryGetProperty("accountId", out var accountId) || accountId.ValueKind != JsonValueKind.String ||
            !account.TryGetProperty("displayName", out var displayName) || displayName.ValueKind != JsonValueKind.String ||
            !account.TryGetProperty("health", out var health) || health.ValueKind != JsonValueKind.String ||
            !account.TryGetProperty("isActive", out var isActive) || isActive.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !account.TryGetProperty("sortOrder", out var sortOrder) || sortOrder.ValueKind != JsonValueKind.Number || !sortOrder.TryGetInt32(out var order) ||
            !account.TryGetProperty("createdAt", out var createdAt) || createdAt.ValueKind != JsonValueKind.String ||
            !account.TryGetProperty("planType", out var planType) || planType.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
        {
            throw new AccountStateShadowCompatibilityException();
        }

        var accountIdText = accountId.GetString();
        var displayNameText = displayName.GetString();
        var healthText = health.GetString();
        var createdAtText = createdAt.GetString();
        var planTypeText = planType.ValueKind == JsonValueKind.Null ? null : planType.GetString();
        if (string.IsNullOrEmpty(accountIdText) || string.IsNullOrEmpty(displayNameText) || string.IsNullOrEmpty(healthText) ||
            string.IsNullOrEmpty(createdAtText) || (planType.ValueKind == JsonValueKind.String && planTypeText is null))
        {
            throw new AccountStateShadowCompatibilityException();
        }

        return new AccountDescriptor(1, accountIdText, displayNameText, healthText, isActive.GetBoolean(), order, createdAtText, planTypeText);
    }

    private static NicknameAuthorityStatus ParseAuthorityStatus(JsonElement value)
    {
        if (!HasExactProperties(value, "state", "epoch", "protocolManifestSha256", "migrationId") ||
            !value.TryGetProperty("migrationId", out var migrationElement) ||
            migrationElement.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            throw new AccountStateShadowCompatibilityException();

        var state = ReadString(value, "state");
        var epoch = ReadInt64(value, "epoch");
        var hash = ReadString(value, "protocolManifestSha256");
        var migrationId = migrationElement.ValueKind == JsonValueKind.Null ? null : migrationElement.GetString();
        if (!NicknameAuthorityStates.IsKnown(state) || epoch < 0 || hash != ProtocolManifestSha256 ||
            migrationId is { Length: 0 or > 256 })
            throw new AccountStateShadowCompatibilityException();
        return new NicknameAuthorityStatus { State = state, Epoch = epoch, ProtocolManifestSha256 = hash, MigrationId = migrationId };
    }

    private static AccountNicknameState ParseNicknameState(JsonElement value)
    {
        if (!HasExactProperties(value, "schemaVersion", "accountId", "nickname", "revision") ||
            ReadInt32(value, "schemaVersion") != 1 ||
            !value.TryGetProperty("nickname", out var nicknameElement) ||
            nicknameElement.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            throw new AccountStateShadowCompatibilityException();

        var accountId = ReadString(value, "accountId");
        var nickname = nicknameElement.ValueKind == JsonValueKind.Null ? null : nicknameElement.GetString();
        var revision = ReadString(value, "revision");
        if (accountId.Length is < 1 or > 256 || revision.Length is < 1 or > 256 ||
            (nickname is not null && (nickname.Length is < 1 or > 256 || string.IsNullOrWhiteSpace(nickname))))
            throw new AccountStateShadowCompatibilityException();
        return new AccountNicknameState { AccountId = accountId, Nickname = nickname, Revision = revision };
    }

    private static bool HasExactProperties(JsonElement element, params string[] requiredNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        var actual = element.EnumerateObject().Select(property => property.Name).ToArray();
        return actual.Length == requiredNames.Length &&
            actual.Distinct(StringComparer.Ordinal).Count() == actual.Length &&
            requiredNames.All(name => actual.Contains(name, StringComparer.Ordinal));
    }

    private static void ValidateSourceCollection(AccountSourceCollection collection)
    {
        if (collection.SchemaVersion != 1 || collection.Accounts is null || collection.Accounts.Count > 1_024)
            throw new AccountStateShadowCompatibilityException();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var account in collection.Accounts)
        {
            if (account is null || account.SchemaVersion != 1 || account.AccountId.Length is < 1 or > 256 ||
                account.SafeFallbackDisplayName.Length is < 1 or > 256 || string.IsNullOrWhiteSpace(account.SafeFallbackDisplayName) ||
                account.SafeFallbackDisplayName.Contains('@') || account.Health is not ("unknown" or "valid" or "needs_relogin") ||
                account.SortOrder < 0 || account.CreatedAt.Length > 64 || !account.CreatedAt.EndsWith('Z') ||
                account.PlanType is { Length: > 128 } || !ids.Add(account.AccountId))
                throw new AccountStateShadowCompatibilityException();
        }
    }

    private static void ValidateMutation(AccountNicknameMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        if (mutation.SchemaVersion != 1 || mutation.AccountId.Length is < 1 or > 256 ||
            mutation.ExpectedRevision.Length is < 1 or > 256 || mutation.SafeFallbackDisplayName.Length is < 1 or > 256 ||
            string.IsNullOrWhiteSpace(mutation.SafeFallbackDisplayName) || mutation.SafeFallbackDisplayName.Contains('@') ||
            (mutation.Nickname is { } nickname &&
                (nickname.Length is < 1 or > 256 || string.IsNullOrWhiteSpace(nickname) || mutation.SafeFallbackDisplayName != "Account")))
            throw new AccountStateShadowCompatibilityException();
    }

    private sealed record RpcRequest(
        [property: JsonPropertyName("jsonrpc")] string JsonRpc,
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("method")] string Method,
        [property: JsonPropertyName("params")] object Parameters);

    private sealed record ReconcileParameters(
        [property: JsonPropertyName("collection")] AccountCollection Collection);

    private sealed record AccountIdParameters(
        [property: JsonPropertyName("accountId")] string AccountId);

    private sealed record AuthorityEpochParameters(
        [property: JsonPropertyName("expectedAuthorityEpoch")] long ExpectedAuthorityEpoch);

    private sealed record AuthorityLifecycleParameters(
        [property: JsonPropertyName("expectedAuthorityEpoch")] long ExpectedAuthorityEpoch,
        [property: JsonPropertyName("migrationId")] string MigrationId);

    private sealed record NicknameMutationParameters(
        [property: JsonPropertyName("mutation")] AccountNicknameMutation Mutation,
        [property: JsonPropertyName("expectedAuthorityEpoch")] long ExpectedAuthorityEpoch,
        [property: JsonPropertyName("migrationId")] string? MigrationId);

    private sealed record SourceReconcileParameters(
        [property: JsonPropertyName("collection")] AccountSourceCollection Collection,
        [property: JsonPropertyName("expectedAuthorityEpoch")] long ExpectedAuthorityEpoch,
        [property: JsonPropertyName("migrationId")] string? MigrationId);
}

internal sealed class AccountStateShadowCompatibilityException : Exception { }

internal sealed class AccountStateShadowRpcException(int code, string? category = null, bool? retryable = null) : Exception
{
    public int Code { get; } = code;

    public string? Category { get; } = category == "storage_failure" && retryable == true
        ? "database_busy"
        : category ?? code switch
        {
            -32601 => "method_not_found",
            -32602 => "validation_failed",
            -32603 => "internal_error",
            -32004 => "not_found",
            -32009 => "revision_conflict",
            -32000 => "storage_failure",
            _ => null
        };

    public bool? Retryable { get; } = retryable;

    public static string? SanitizeCategory(string? category) => category switch
    {
        "not_found" or "revision_conflict" or "validation_failed" or "method_not_found" or
        "unsupported_schema_version" or "database_unhealthy" or "database_busy" or "resource_limit_exceeded" or
        "authority_state_conflict" or "legacy_operation_fenced" or "experimental_feature_disabled" or
        "storage_failure" or "internal_error" => category,
        _ => null
    };
}

internal sealed class AccountStateShadowRpcTimeoutException : TimeoutException { }
internal sealed class AccountStateShadowRpcOutcomeUnknownException : IOException { }
internal sealed class AccountStateShadowRpcRequestNotSentException : IOException { }
