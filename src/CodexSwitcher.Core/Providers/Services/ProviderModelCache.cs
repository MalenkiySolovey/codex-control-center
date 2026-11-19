using CodexSwitcher.Core.Providers.Contracts;

namespace CodexSwitcher.Core.Providers.Services;

/// <summary>Maps product profile IDs onto the provider kit's endpoint/route/revision cache.</summary>
public sealed class ProviderModelCache : IProviderModelCache
{
    private readonly Llm.ProviderKit.Models.ProviderModelCache _cache = new();

    public bool TryGetModels(Guid profileId, string routeKey, int revision, out IReadOnlyList<string> models) =>
        _cache.TryGet(profileId.ToString("N"), routeKey, revision, out models);

    public void SetModels(Guid profileId, string routeKey, int revision, IReadOnlyList<string> models) =>
        _cache.Set(profileId.ToString("N"), routeKey, revision, models);

    public void Invalidate(Guid profileId, string? routeKey = null) =>
        _cache.Invalidate(profileId.ToString("N"), routeKey);

    public IReadOnlyList<string>? GetLatestModels(Guid profileId) =>
        _cache.GetLatest(profileId.ToString("N"));
}
