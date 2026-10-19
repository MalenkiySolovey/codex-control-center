using System.Text.Json;
using System.Text.Json.Nodes;
using System.Globalization;
using System.Text.RegularExpressions;
using CodexSwitcher.Core.AgentState.V1;
using Json.Schema;

namespace CodexSwitcher.Core.Tests;

public sealed class AgentStateContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    [Fact]
    public void EveryDraft202012SchemaAndCanonicalExampleValidates()
    {
        var schemaDirectory = Path.Combine(FindRepositoryRoot(), "contracts", "agent-state", "v1", "schemas");
        var exampleDirectory = Path.Combine(FindRepositoryRoot(), "contracts", "agent-state", "v1", "examples");
        var schemaFiles = Directory.EnumerateFiles(schemaDirectory, "*.schema.json", SearchOption.AllDirectories).ToArray();

        Assert.NotEmpty(schemaFiles);
        foreach (var schemaPath in schemaFiles)
        {
            var schemaJson = JsonNode.Parse(File.ReadAllText(schemaPath))!;
            Assert.Equal("https://json-schema.org/draft/2020-12/schema", schemaJson["$schema"]?.GetValue<string>());
            using var schemaDocument = JsonDocument.Parse(schemaJson.ToJsonString());
            var metaResult = MetaSchemas.Draft202012.Evaluate(schemaDocument.RootElement);
            Assert.True(metaResult.IsValid, $"Schema is not valid Draft 2020-12: {Path.GetFileName(schemaPath)}");

            var schema = LoadSchema(schemaPath);
            var exampleName = Path.GetFileName(schemaPath).Replace(".schema.json", ".example.json", StringComparison.Ordinal);
            var examplePath = Path.Combine(exampleDirectory, exampleName);
            Assert.True(File.Exists(examplePath), $"Canonical example missing for {Path.GetFileName(schemaPath)}");
            var example = JsonNode.Parse(File.ReadAllText(examplePath))!;
            AssertValid(schema, example, exampleName);
        }
    }

    [Fact]
    public void AccountProjection_UsesSafeCurrentDomainFieldsAndIsDeterministic()
    {
        var schema = LoadSchema(GetSchemaPath("accounts", "account-descriptor.schema.json"));
        var profile = new ProfileMetadata
        {
            Id = Guid.Parse("b1dd47b1-5029-40c2-a7cc-84e78c8756df"),
            Nickname = "person@example.invalid",
            DetectedDisplayName = "Example User",
            PlanType = "plus",
            HealthStatus = HealthStatus.Valid,
            IsActive = true,
            SortOrder = 3,
            CreatedAt = new DateTimeOffset(2026, 10, 1, 15, 30, 0, TimeSpan.FromHours(3)),
        };

        var descriptor = AccountDescriptorProjector.FromProfile(profile, ordinal: 4);
        var first = JsonSerializer.Serialize(descriptor, JsonOptions);
        var second = JsonSerializer.Serialize(descriptor, JsonOptions);
        var json = JsonNode.Parse(first)!;

        Assert.Equal(first, second);
        Assert.Equal("Example User", descriptor.DisplayName);
        Assert.DoesNotContain("person@example.invalid", first, StringComparison.Ordinal);
        Assert.EndsWith("Z", descriptor.CreatedAt, StringComparison.Ordinal);
        Assert.Equal(TimeSpan.Zero, DateTimeOffset.Parse(descriptor.CreatedAt, CultureInfo.InvariantCulture).Offset);
        AssertValid(schema, json, "projected account descriptor");
    }

    [Fact]
    public void ProtocolVersionAndUnknownFieldPolicyAreExplicit()
    {
        var accountSchema = LoadSchema(GetSchemaPath("accounts", "account-descriptor.schema.json"));
        var account = ReadExample("account-descriptor.example.json");
        account["schemaVersion"] = 2;
        AssertInvalid(accountSchema, account, "schemaVersion mismatch");

        var closedSchema = LoadSchema(GetSchemaPath("accounts", "account-descriptor.schema.json"));
        var unknownFieldAccount = ReadExample("account-descriptor.example.json");
        unknownFieldAccount["futureField"] = "not in v1";
        AssertInvalid(closedSchema, unknownFieldAccount, "unknown stable field");

        var capabilitySchema = LoadSchema(GetSchemaPath("common", "capability-descriptor.schema.json"));
        var capability = ReadExample("capability-descriptor.example.json");
        capability["extensions"]!["future-scalar"] = true;
        AssertValid(capabilitySchema, capability, "controlled capability extension");
    }

    [Fact]
    public void StableEnumsRemainStringsAndUtcTimestampsUseRfc3339()
    {
        var descriptor = AccountDescriptorProjector.FromProfile(
            new ProfileMetadata
            {
                Id = Guid.Parse("00000000-0000-0000-0000-000000000001"),
                Nickname = "Example",
                HealthStatus = HealthStatus.NeedsReLogin,
                CreatedAt = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero),
            },
            ordinal: 1);
        var json = JsonDocument.Parse(JsonSerializer.Serialize(descriptor, JsonOptions)).RootElement;

        Assert.Equal(JsonValueKind.String, json.GetProperty("health").ValueKind);
        Assert.Equal("needs_relogin", json.GetProperty("health").GetString());
        Assert.Equal(JsonValueKind.Number, json.GetProperty("schemaVersion").ValueKind);
        Assert.True(DateTimeOffset.TryParse(json.GetProperty("createdAt").GetString(), out var timestamp));
        Assert.Equal(TimeSpan.Zero, timestamp.Offset);
        Assert.EndsWith("Z", json.GetProperty("createdAt").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ErrorDetailRedactsSecretMaterialFromMessagesAndDetails()
    {
        const string apiKey = "sk-test_1234567890abcdef";
        const string bearer = "Bearer access-secret-value";
        const string jwt = "eyJhbGciOiJub25lIn0.eyJzdWIiOiJkZW1vIn0.signaturevalue123";
        var error = ErrorDetailFactory.FromMessage(
            "credential.unavailable",
            $"Request failed with {bearer}; api_key={apiKey}; access_token={jwt}",
            new Dictionary<string, string>
            {
                ["apiKey"] = apiKey,
                ["operation"] = $"refresh {bearer}",
            });
        var json = JsonSerializer.Serialize(error, JsonOptions);

        Assert.DoesNotContain(apiKey, json, StringComparison.Ordinal);
        Assert.DoesNotContain("access-secret-value", json, StringComparison.Ordinal);
        Assert.DoesNotContain(jwt, json, StringComparison.Ordinal);
        Assert.DoesNotContain("apiKey", json, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", json, StringComparison.Ordinal);
        AssertValid(LoadSchema(GetSchemaPath("common", "error-detail.schema.json")), JsonNode.Parse(json)!, "sanitized error");
    }

    [Fact]
    public void GeneralStateExamplesContainNoSecretValuesOrPersonalEmailAddresses()
    {
        var examples = Path.Combine(FindRepositoryRoot(), "contracts", "agent-state", "v1", "examples");
        var forbiddenNames = new[]
        {
            "access_token", "refresh_token", "id_token", "api_key", "apikey", "totpsecret",
            "secretvalue", "rawauthjson", "vaultpath", "plaintextsecret", "accesstoken", "refreshtoken",
        };
        var emailPattern = new Regex(@"\b[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}\b", RegexOptions.CultureInvariant);
        foreach (var examplePath in Directory.EnumerateFiles(examples, "*.json", SearchOption.AllDirectories))
        {
            var content = File.ReadAllText(examplePath);
            Assert.DoesNotMatch(emailPattern, content);
            var document = JsonNode.Parse(content)!;
            foreach (var propertyName in GetPropertyNames(document))
                Assert.DoesNotContain(propertyName.Replace("_", string.Empty, StringComparison.Ordinal), forbiddenNames, StringComparer.OrdinalIgnoreCase);
        }
    }

    private static JsonSchema LoadSchema(string path) => JsonSchema.FromFile(
        path,
        new BuildOptions
        {
            Dialect = Dialect.Draft202012,
            SchemaRegistry = new SchemaRegistry(),
        });

    private static JsonNode ReadExample(string name) => JsonNode.Parse(File.ReadAllText(
        Path.Combine(FindRepositoryRoot(), "contracts", "agent-state", "v1", "examples", name)))!;

    private static string GetSchemaPath(string group, string name) => Path.Combine(
        FindRepositoryRoot(), "contracts", "agent-state", "v1", "schemas", group, name);

    private static void AssertValid(JsonSchema schema, JsonNode instance, string label)
    {
        using var document = JsonDocument.Parse(instance.ToJsonString());
        var result = schema.Evaluate(document.RootElement, new EvaluationOptions { RequireFormatValidation = true });
        Assert.True(result.IsValid, $"Expected valid JSON Schema instance: {label}");
    }

    private static void AssertInvalid(JsonSchema schema, JsonNode instance, string label)
    {
        using var document = JsonDocument.Parse(instance.ToJsonString());
        var result = schema.Evaluate(document.RootElement, new EvaluationOptions { RequireFormatValidation = true });
        Assert.False(result.IsValid, $"Expected invalid JSON Schema instance: {label}");
    }

    private static IEnumerable<string> GetPropertyNames(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var entry in obj)
            {
                yield return entry.Key;
                if (entry.Value is not null)
                {
                    foreach (var nested in GetPropertyNames(entry.Value))
                        yield return nested;
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is not null)
                {
                    foreach (var nested in GetPropertyNames(item))
                        yield return nested;
                }
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "contracts", "agent-state", "v1")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate the Agent State v1 contract staging directory.");
    }
}
