using CodexSwitcher.Infra.Common.Paths;
using CodexSwitcher.Infra.Common.Storage;
using CodexSwitcher.Infra.Security.Hardening;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using CodexSwitcher.Core.Common.Lifecycle;

namespace CodexSwitcher.App.Composition;

/// <summary>
/// Composition factory that constructs the .NET Generic Host for Codex Switchboard.
/// Configures a minimal host with DisableDefaults to prevent reading configuration or secrets
/// from environment variables, CLI args, or appsettings.json.
/// Exposes NO static service locator properties.
/// </summary>
public static class SwitchboardHostBuilder
{
    public static HostApplicationBuilder CreateApplicationBuilder(AppPaths? paths = null, bool validateContainer = false)
    {
        var appPaths = paths ?? new AppPaths();
        appPaths.EnsureDirectories();
        DirectoryHardening.TryRestrictToCurrentUser(appPaths.Root);
        StartupTracer.Instance.RecordMilestone("T4a:AppDirectoriesReady");

        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true
        });

        // Enforce strict container validation in test environments; skip reflection penalty in production
        if (validateContainer)
        {
            builder.Services.Configure<ServiceProviderOptions>(options =>
            {
                options.ValidateOnBuild = true;
                options.ValidateScopes = true;
            });
        }

        builder.Services.AddSwitchboardServices(appPaths);
        StartupTracer.Instance.RecordMilestone("T4b:ServiceRegistrationCompleted");

        return builder;
    }

    public static IHost CreateHost(
        AppPaths? paths,
        Action<IServiceCollection>? configureServices,
        bool validateContainer)
    {
        var builder = CreateApplicationBuilder(paths, validateContainer);
        configureServices?.Invoke(builder.Services);
        StartupTracer.Instance.RecordMilestone("T4c:HostBuildStarted");
        var host = builder.Build();
        StartupTracer.Instance.RecordMilestone("T4d:HostBuildCompleted");
        return host;
    }

    public static IHost CreateHost(
        AppPaths? paths = null,
        Action<IServiceCollection>? configureServices = null)
    {
        return CreateHost(paths, configureServices, validateContainer: false);
    }
}
