namespace CodexSwitcher.Core.Tests;

internal static class CatalogTestPaths
{
    private static readonly string RepositoryRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..",
        "..",
        "..",
        "..",
        ".."));

    public static string CatalogPath => Path.Combine(
        RepositoryRoot,
        "artifacts",
        "provider-catalog",
        "providers.catalog.json");

    public static string SchemaPath => Path.Combine(
        RepositoryRoot,
        "artifacts",
        "provider-catalog",
        "providers.catalog.schema.json");

    public static string SignaturePath => Path.Combine(
        RepositoryRoot,
        "artifacts",
        "provider-catalog",
        "providers.catalog.sig");
}
