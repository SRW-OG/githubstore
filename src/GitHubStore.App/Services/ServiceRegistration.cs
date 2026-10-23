using GitHubStore.App.Services.Download;
using GitHubStore.App.Services.Inventory;
using GitHubStore.App.Services.Localization;
using GitHubStore.App.Services.Preferences;
using GitHubStore.App.Services.Processes;
using GitHubStore.App.Services.Tools;
using GitHubStore.App.Services.Winget;
using GitHubStore.App.ViewModels;
using GitHubStore.Core.Abstractions;
using GitHubStore.Core.Catalog;
using GitHubStore.Platform.Win32.Runtime;
using GitHubStore.Platform.Win32.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace GitHubStore.App.Services;

public static class ServiceRegistration
{
    public static string DataDirectory { get; } = ResolveDataDirectory();

    private static string ResolveDataDirectory()
    {
        var overridden = Environment.GetEnvironmentVariable("GITHUBSTORE_DATA_DIR");
        return string.IsNullOrWhiteSpace(overridden)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GitHubStore")
            : Path.GetFullPath(overridden.Trim());
    }

    public static ServiceProvider CreateProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new PreferencesService(Path.Combine(DataDirectory, "settings.json")));
        services.AddSingleton(_ => new LocalizationService("system"));
        services.AddSingleton(sp => new PackageCacheService(
            () => StorageLocations.ResolveCacheDirectory(sp.GetRequiredService<PreferencesService>().Current, DataDirectory)));
        services.AddSingleton(sp =>
        {
            var http = new HttpClient(new PreferencesHttpHandler(sp.GetRequiredService<PreferencesService>()))
            { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("GitHubStore/0.1");
            return http;
        });
        // The curated catalog ships with the app; user cache and deletions still take precedence.
        services.AddSingleton(sp => new GitHubCatalogService(sp.GetRequiredService<HttpClient>(), DataDirectory,
            includeCuratedSeed: true));
        services.AddSingleton<ICatalogService>(sp => sp.GetRequiredService<GitHubCatalogService>());
        services.AddSingleton<ICatalogLibrarySyncService>(sp => sp.GetRequiredService<GitHubCatalogService>());
        services.AddSingleton<ISearchService>(sp => sp.GetRequiredService<GitHubCatalogService>());
        services.AddSingleton<IRepositoryCatalogService>(sp => sp.GetRequiredService<GitHubCatalogService>());
        services.AddSingleton<WindowsRuntimeInfo>();
        services.AddSingleton<IManagedAppLauncher>(sp => new GitHubStore.App.Services.Launcher.ManagedAppLauncher(DataDirectory,
            installRootProvider: () => StorageLocations.ResolveInstallRoot(sp.GetRequiredService<PreferencesService>().Current)));
        services.AddSingleton<IAppInstallService>(sp => new GitHubStore.App.Services.Installation.AppInstallService(
            sp.GetRequiredService<IDownloadService>(), sp.GetRequiredService<IManagedAppLauncher>(),
            sp.GetRequiredService<PreferencesService>(), DataDirectory,
            packageCache: sp.GetRequiredService<PackageCacheService>()));
        services.AddSingleton<InstallationViewModel>();
        services.AddSingleton<WindowController>();
        services.AddSingleton(new OperationLog(DataDirectory));
        // 移除服务由本服务在内部构造：它需要安装根目录校验删除目标，而启动器是同一个单例。
        services.AddSingleton<IInstalledProgramService>(sp =>
            new ManagedInstalledProgramService(sp.GetRequiredService<IManagedAppLauncher>()));
        services.AddSingleton<IToolManager>(_ => new ManagedToolManager(DataDirectory,
            new BundledToolPackageSource(typeof(ServiceRegistration).Assembly, "GitHubStore.Tools.tools.manifest.json"),
            WindowsSystemToolLocator.ResolveWingetAsync));
        services.AddSingleton<ConfiguredDownloadService>();
        services.AddSingleton<IDownloadService>(sp => sp.GetRequiredService<ConfiguredDownloadService>());
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IWingetService>(sp => new WingetService(sp.GetRequiredService<IProcessRunner>(),
            tools: sp.GetRequiredService<IToolManager>()));
        services.AddSingleton<SourcesViewModel>();
        services.AddSingleton<DownloadsViewModel>();
        services.AddSingleton<GitHubStore.App.Services.Ai.IAiClient>(sp =>
            new GitHubStore.App.Services.Ai.OpenAiClient(sp.GetRequiredService<PreferencesService>()));
        services.AddSingleton<GitHubStore.App.Services.Ai.IReadmeSummaryService>(sp =>
            new GitHubStore.App.Services.Ai.ReadmeSummaryService(sp.GetRequiredService<HttpClient>(),
                sp.GetRequiredService<PreferencesService>(), DataDirectory,
                client: sp.GetRequiredService<GitHubStore.App.Services.Ai.IAiClient>()));
        services.AddSingleton<GitHubStore.App.Services.Ai.IAiAssistantService>(sp =>
            new GitHubStore.App.Services.Ai.AiAssistantService(
                sp.GetRequiredService<GitHubStore.App.Services.Ai.IAiClient>(),
                sp.GetRequiredService<ICatalogService>(), sp.GetRequiredService<IManagedAppLauncher>(),
                sp.GetRequiredService<GitHubStore.App.Services.Ai.IReadmeSummaryService>()));
        services.AddSingleton<GitHubStore.App.Services.Ai.IChineseReadmeService>(sp =>
            new GitHubStore.App.Services.Ai.ChineseReadmeService(
                sp.GetRequiredService<GitHubStore.App.Services.Ai.IReadmeSummaryService>(),
                sp.GetRequiredService<GitHubStore.App.Services.Ai.IAiClient>(),
                sp.GetRequiredService<PreferencesService>(), DataDirectory));
        services.AddSingleton<IDiscoveryFeedService>(sp =>
            new GitHubStore.App.Services.Discovery.DiscoveryFeedService(sp.GetRequiredService<HttpClient>(), DataDirectory));
        services.AddSingleton<IDiscoveryReviewService>(sp =>
            new GitHubStore.App.Services.Discovery.DiscoveryReviewService(sp.GetRequiredService<GitHubStore.App.Services.Ai.IAiClient>()));
        services.AddSingleton<DiscoveryHomeViewModel>();
        services.AddSingleton<CatalogViewModel>();
        services.AddSingleton<LauncherViewModel>(sp => new LauncherViewModel(
            sp.GetRequiredService<IManagedAppLauncher>(), sp.GetRequiredService<LocalizationService>(),
            sp.GetRequiredService<ICatalogService>()));
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<InstalledViewModel>();
        services.AddTransient<AddRepositoryViewModel>();
        services.AddTransient<ShellViewModel>();
        services.AddTransient<MainPage>();
        services.AddTransient<MainWindow>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
}
