using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexSwitcher.Core.AgentState.V1;

namespace CodexSwitcher.Infra.AgentState.Shadow;

internal sealed class AccountStateShadowRpcClient : IAccountStateShadowClient
{
    internal const int MaximumFrameBytes = 1_048_576;
    internal const string ProtocolManifestSha256 = "85215509e03709696f44e775728a9d2de7d5b70b6ab174c0e59f4ad4af2a4d36";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { MaxDepth = 32 };
    private static readonly byte[] NewLine = [(byte)'\n'];

    private readonly NamedPipeClientStream _pipe;
    private int _requestId;

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
        {
            if (account.ValueKind != JsonValueKind.Object ||
                !account.TryGetProperty("schemaVersion", out var itemVersion) ||
                itemVersion.ValueKind != JsonValueKind.Number || !itemVersion.TryGetInt32(out version) || version != 1 ||
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

            projected.Add(new AccountDescriptor(
                1,
                accountIdText,
                displayNameText,
                healthText,
                isActive.GetBoolean(),
                order,
                createdAtText,
                planTypeText));
        }

        return projected;
    }

    public async ValueTask DisposeAsync() => await _pipe.DisposeAsync().ConfigureAwait(false);

    private async Task<JsonElement> CallAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        if (!_pipe.IsConnected)
            throw new IOException("The shadow state connection is unavailable.");

        var requestId = Interlocked.Increment(ref _requestId);
        var request = JsonSerializer.SerializeToUtf8Bytes(new RpcRequest("2.0", requestId, method, parameters), JsonOptions);
        if (request.Length > MaximumFrameBytes)
            throw new InvalidDataException("The shadow state request exceeds the supported frame size.");

        await _pipe.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        await _pipe.WriteAsync(NewLine, cancellationToken).ConfigureAwait(false);
        await _pipe.FlushAsync(cancellationToken).ConfigureAwait(false);

        var responseBytes = await ReadFrameAsync(cancellationToken).ConfigureAwait(false);
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
            throw new AccountStateShadowRpcException(code);
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

    private static string ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()!
            : throw new AccountStateShadowCompatibilityException();

    private sealed record RpcRequest(
        [property: JsonPropertyName("jsonrpc")] string JsonRpc,
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("method")] string Method,
        [property: JsonPropertyName("params")] object Parameters);

    private sealed record ReconcileParameters(
        [property: JsonPropertyName("collection")] AccountCollection Collection);
}

internal sealed class AccountStateShadowCompatibilityException : Exception { }

internal sealed class AccountStateShadowRpcException(int code) : Exception
{
    public int Code { get; } = code;
}
