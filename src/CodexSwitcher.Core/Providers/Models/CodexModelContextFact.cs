using System;

namespace CodexSwitcher.Core.Providers.Models;

/// <summary>
/// Expanded model context facts distinguishing:
/// documented model max,
/// route advertised/verified max,
/// requested context,
/// Codex client context,
/// and effective usable context.
/// </summary>
public sealed record CodexModelContextFact(
    string ModelSlug,
    long? DocumentedModelMax,
    long? RouteAdvertisedMax,
    long? RequestedContext,
    long? CodexClientContext,
    bool IsKnown,
    string SourceDescription)
{
    public long? DocumentedMaxContext => DocumentedModelMax;

    /// <summary>
    /// Computes the safe effective usable context window:
    /// bounded by the model documented ceiling, route verified ceiling, and requested context.
    /// </summary>
    public long? EffectiveUsableContext
    {
        get
        {
            var ceiling = (DocumentedModelMax, RouteAdvertisedMax) switch
            {
                (long documented, long advertised) => Math.Min(documented, advertised),
                (long documented, null) => documented,
                (null, long advertised) => advertised,
                _ => (long?)null,
            };

            if (!ceiling.HasValue)
            {
                return RequestedContext ?? CodexClientContext;
            }

            if (RequestedContext.HasValue)
            {
                return Math.Min(RequestedContext.Value, ceiling.Value);
            }

            return ceiling;
        }
    }

    public static CodexModelContextFact FromLimitFact(
        CodexModelLimitFact fact,
        long? routeAdvertisedMax = null,
        long? requestedContext = null,
        long? codexClientContext = null)
    {
        return new CodexModelContextFact(
            fact.ModelSlug,
            fact.DocumentedMaxContext,
            routeAdvertisedMax,
            requestedContext,
            codexClientContext,
            fact.IsKnown,
            fact.SourceDescription);
    }
}
