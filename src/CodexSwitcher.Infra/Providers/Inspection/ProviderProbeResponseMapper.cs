using System.Text.Json;
using CodexSwitcher.Core.Providers.Catalog;
using CodexSwitcher.Core.Providers.Services;

namespace CodexSwitcher.Infra.Providers.Inspection;

/// <summary>
/// Production implementation of <see cref="IProviderProbeResponseMapper"/> extracting
/// normalized models and credit balances using <see cref="JsonPointerExtractor"/>.
/// </summary>
public sealed class ProviderProbeResponseMapper : IProviderProbeResponseMapper
{
    public (bool Success, List<string>? Models, string? Error) MapModelsResponse(
        ProbeHttpResponse response,
        string? jsonPointer)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (!response.Success || response.Json == null)
        {
            return (false, null, response.Error);
        }

        var root = response.Json.Value;
        var effectivePointer = jsonPointer ?? "/data";

        if (JsonPointerExtractor.TryExtractStringArray(root, effectivePointer, out var models))
        {
            return (true, models, null);
        }

        return (true, [], null);
    }

    public void MapBalanceAndUsageResponse(
        ProbeHttpResponse response,
        RecipeResponseMapping? mapping,
        ref decimal? balance,
        ref decimal? usedCredits,
        ref decimal? creditLimit,
        ref decimal? remainingCredits,
        ref string? currency)
    {
        ArgumentNullException.ThrowIfNull(response);

        MapBalanceAndUsageResponses(
            new Dictionary<string, ProbeHttpResponse>(StringComparer.Ordinal) { ["default"] = response },
            mapping,
            ref balance,
            ref usedCredits,
            ref creditLimit,
            ref remainingCredits,
            ref currency);
    }

    /// <summary>
    /// Maps a bounded set of named read-only responses. Numeric outputs may be
    /// direct JSON pointers or references to other numeric outputs using only
    /// scale, add, and subtract operations.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "This instance method implements the injected mapper contract.")]
    public void MapBalanceAndUsageResponses(
        IReadOnlyDictionary<string, ProbeHttpResponse> responses,
        RecipeResponseMapping? mapping,
        ref decimal? balance,
        ref decimal? usedCredits,
        ref decimal? creditLimit,
        ref decimal? remainingCredits,
        ref string? currency)
    {
        ArgumentNullException.ThrowIfNull(responses);
        if (mapping == null)
        {
            return;
        }

        var outputs = new Dictionary<string, JsonFieldMapping?>(StringComparer.OrdinalIgnoreCase)
        {
            ["balance"] = mapping.Balance,
            ["used"] = mapping.Used,
            ["limit"] = mapping.Limit,
            ["remaining"] = mapping.Remaining,
        };
        var resolved = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var resolving = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (TryResolveOutput("balance", outputs, responses, resolved, resolving, out var b))
        {
            balance = b;
        }

        if (TryResolveOutput("used", outputs, responses, resolved, resolving, out var u))
        {
            usedCredits = u;
        }

        if (TryResolveOutput("limit", outputs, responses, resolved, resolving, out var l))
        {
            creditLimit = l;
        }

        if (TryResolveOutput("remaining", outputs, responses, resolved, resolving, out var r))
        {
            remainingCredits = r;
        }

        if (mapping.Currency != null)
        {
            if (!string.IsNullOrWhiteSpace(mapping.Currency.Literal))
            {
                currency = mapping.Currency.Literal.Trim();
            }
            else if (!string.IsNullOrWhiteSpace(mapping.Currency.Pointer) &&
                     TryGetResponse(responses, mapping.Currency.RequestId, out var currencyResponse) &&
                     currencyResponse.Json is { } currencyJson &&
                     JsonPointerExtractor.TryExtractString(currencyJson, mapping.Currency.Pointer, out var c) &&
                     !string.IsNullOrWhiteSpace(c))
            {
                currency = c.Trim();
            }
        }
    }

    private static bool TryResolveOutput(
        string outputName,
        IReadOnlyDictionary<string, JsonFieldMapping?> outputs,
        IReadOnlyDictionary<string, ProbeHttpResponse> responses,
        IDictionary<string, decimal> resolved,
        ISet<string> resolving,
        out decimal value)
    {
        value = 0m;
        if (resolved.TryGetValue(outputName, out value))
        {
            return true;
        }

        if (!outputs.TryGetValue(outputName, out var mapping) || mapping is null || !resolving.Add(outputName))
        {
            return false;
        }

        try
        {
            var operation = string.IsNullOrWhiteSpace(mapping.Operation)
                ? "direct"
                : mapping.Operation.Trim().ToLowerInvariant();
            var sources = mapping.Sources ?? [];

            switch (operation)
            {
                case "direct":
                    if (sources.Count != 0 || string.IsNullOrWhiteSpace(mapping.Pointer) ||
                        !TryGetResponse(responses, mapping.RequestId, out var directResponse) ||
                        directResponse.Json is not { } directJson ||
                        !JsonPointerExtractor.TryExtractDecimal(directJson, mapping, out value))
                    {
                        return false;
                    }
                    break;

                case "scale":
                    decimal sourceValue;
                    if (sources.Count == 1)
                    {
                        if (!TryResolveOutput(sources[0], outputs, responses, resolved, resolving, out sourceValue))
                        {
                            return false;
                        }
                    }
                    else if (sources.Count == 0 && !string.IsNullOrWhiteSpace(mapping.Pointer) &&
                             TryGetResponse(responses, mapping.RequestId, out var scaleResponse) &&
                             scaleResponse.Json is { } scaleJson &&
                             JsonPointerExtractor.TryExtractDecimal(scaleJson, mapping, out sourceValue))
                    {
                        // A direct source keeps the catalog's legacy scale and
                        // centsToUnits transform support.
                    }
                    else
                    {
                        return false;
                    }

                    value = checked(sourceValue * (mapping.ScaleFactor ?? 1m));
                    break;

                case "add":
                case "subtract":
                    if (sources.Count != 2 ||
                        !TryResolveOutput(sources[0], outputs, responses, resolved, resolving, out var left) ||
                        !TryResolveOutput(sources[1], outputs, responses, resolved, resolving, out var right))
                    {
                        return false;
                    }

                    value = operation == "add" ? checked(left + right) : checked(left - right);
                    break;

                default:
                    return false;
            }

            resolved[outputName] = value;
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
        finally
        {
            resolving.Remove(outputName);
        }
    }

    private static bool TryGetResponse(
        IReadOnlyDictionary<string, ProbeHttpResponse> responses,
        string? requestId,
        out ProbeHttpResponse response)
    {
        response = default!;
        if (!string.IsNullOrWhiteSpace(requestId))
        {
            return responses.TryGetValue(requestId, out response!) && response.Success && response.Json.HasValue;
        }

        if (responses.Count != 1)
        {
            return false;
        }

        response = responses.Values.First();
        return response.Success && response.Json.HasValue;
    }
}
