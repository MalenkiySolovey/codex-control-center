using System.Reflection;
using System.Xml.Linq;

namespace CodexSwitcher.Core.Tests;

public sealed class ArchitectureContractGuardTests
{
    [Fact]
    public void Core_HasNoApplicationOrInfrastructureProjectReferencesOrUiPublicTypes()
    {
        var root = FindRepositoryRoot();
        var coreProject = LoadProject(Path.Combine(root, "src", "CodexSwitcher.Core", "CodexSwitcher.Core.csproj"));
        var projectReferences = GetProjectReferences(coreProject);

        Assert.DoesNotContain("CodexSwitcher.App", projectReferences);
        Assert.DoesNotContain("CodexSwitcher.Infra", projectReferences);

        var forbidden = new HashSet<string>(StringComparer.Ordinal)
        {
            "Microsoft.UI.Xaml",
            "Microsoft.UI.Dispatching.DispatcherQueue",
            "Microsoft.UI.Windowing.AppWindow",
            "System.IServiceProvider",
            "System.Windows.Window",
        };
        var publicTypes = typeof(AccountRegistry).Assembly.GetExportedTypes();
        var exposedTypes = GetPublicApiTypes(publicTypes);
        var violations = exposedTypes
            .SelectMany(ExpandType)
            .Select(type => type.FullName ?? type.Name)
            .Where(name => forbidden.Contains(name) || name.StartsWith("Microsoft.UI.Xaml.", StringComparison.Ordinal))
            .Distinct()
            .ToArray();

        Assert.Empty(violations);
        var reusableAccountTypes = publicTypes.Where(type =>
            type.Namespace?.StartsWith("CodexSwitcher.Core.Accounts.Contracts", StringComparison.Ordinal) == true ||
            type.Namespace?.StartsWith("CodexSwitcher.Core.Accounts.Models", StringComparison.Ordinal) == true ||
            type.Namespace?.StartsWith("CodexSwitcher.Core.AgentState.V1", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(GetPublicApiTypes(reusableAccountTypes).SelectMany(ExpandType),
            type => type.FullName == "System.Diagnostics.Process");
        Assert.DoesNotContain(typeof(AccountRegistry).Assembly.GetReferencedAssemblies(),
            reference => reference.Name?.StartsWith("Microsoft.UI", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void InfrastructureAndApplication_KeepTheIntendedDependencyDirection()
    {
        var root = FindRepositoryRoot();
        var infraReferences = GetProjectReferences(LoadProject(Path.Combine(root, "src", "CodexSwitcher.Infra", "CodexSwitcher.Infra.csproj")));
        var appReferences = GetProjectReferences(LoadProject(Path.Combine(root, "src", "CodexSwitcher.App", "CodexSwitcher.App.csproj")));

        Assert.Contains("CodexSwitcher.Core", infraReferences);
        Assert.DoesNotContain("CodexSwitcher.App", infraReferences);
        Assert.Contains("CodexSwitcher.Core", appReferences);
        Assert.Contains("CodexSwitcher.Infra", appReferences);
    }

    [Fact]
    public void ProfileService_HasNoMutableProfilesOrSaveSurface_AndNoProductionConsumers()
    {
        var profileService = typeof(ProfileService);
        Assert.Null(profileService.GetProperty("Profiles", BindingFlags.Public | BindingFlags.Instance));
        Assert.Null(profileService.GetMethod("Save", BindingFlags.Public | BindingFlags.Instance));

        var root = FindRepositoryRoot();
        var source = Path.Combine(root, "src");
        var compatibilityFile = Path.GetFullPath(Path.Combine(source, "CodexSwitcher.Core", "Accounts", "Services", "ProfileService.cs"));
        var consumers = Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
            .Where(path => !string.Equals(Path.GetFullPath(path), compatibilityFile, StringComparison.OrdinalIgnoreCase))
            .Where(path => File.ReadAllText(path).Contains("ProfileService", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(root, path))
            .ToArray();

        Assert.Empty(consumers);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "CodexSwitcher.Core")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate the Codex Control Center repository root from the test output directory.");
    }

    private static XDocument LoadProject(string path) => XDocument.Load(path);

    private static string[] GetProjectReferences(XDocument project) => project
        .Descendants()
        .Where(element => element.Name.LocalName == "ProjectReference")
        .Select(element => (string?)element.Attribute("Include"))
        .Where(include => include is not null)
        .Select(include => Path.GetFileNameWithoutExtension(include!))
        .ToArray();

    private static IEnumerable<Type> GetPublicApiTypes(IEnumerable<Type> publicTypes)
    {
        foreach (var type in publicTypes)
        {
            yield return type;

            foreach (var constructor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
                foreach (var parameter in constructor.GetParameters())
                    yield return parameter.ParameterType;

            foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                switch (member)
                {
                    case PropertyInfo property:
                        yield return property.PropertyType;
                        break;
                    case FieldInfo field:
                        yield return field.FieldType;
                        break;
                    case EventInfo eventInfo when eventInfo.EventHandlerType is not null:
                        yield return eventInfo.EventHandlerType;
                        break;
                    case MethodInfo method:
                        yield return method.ReturnType;
                        foreach (var parameter in method.GetParameters())
                            yield return parameter.ParameterType;
                        break;
                }
            }
        }
    }

    private static IEnumerable<Type> ExpandType(Type type)
    {
        if (type.HasElementType && type.GetElementType() is { } elementType)
            yield return elementType;

        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments())
                yield return argument;
        }

        yield return type;
    }
}
