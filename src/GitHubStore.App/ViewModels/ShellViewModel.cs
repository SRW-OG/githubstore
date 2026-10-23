using CommunityToolkit.Mvvm.ComponentModel;
using GitHubStore.App.Services.Localization;

namespace GitHubStore.App.ViewModels;

public enum ShellSection { Home, Catalog, Launcher, Installed, Sources, Settings }

public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    public ShellViewModel(CatalogViewModel catalog, LauncherViewModel launcher, InstalledViewModel installed,
        SourcesViewModel sources, SettingsViewModel settings, LocalizationService text, DiscoveryHomeViewModel home)
    {
        Home = home;
        Catalog = catalog;
        Launcher = launcher;
        Installed = installed;
        Sources = sources;
        Settings = settings;
        Text = text;
    }

    public DiscoveryHomeViewModel Home { get; }
    public CatalogViewModel Catalog { get; }
    public LauncherViewModel Launcher { get; }
    public InstalledViewModel Installed { get; }
    public SourcesViewModel Sources { get; }
    public SettingsViewModel Settings { get; }
    public LocalizationService Text { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHome), nameof(IsCatalog), nameof(IsLauncher), nameof(IsInstalled), nameof(IsSources), nameof(IsSettings))]
    private ShellSection _section = ShellSection.Home;

    public bool IsHome => Section == ShellSection.Home;
    public bool IsCatalog => Section == ShellSection.Catalog;
    public bool IsLauncher => Section == ShellSection.Launcher;
    public bool IsInstalled => Section == ShellSection.Installed;
    public bool IsSources => Section == ShellSection.Sources;
    public bool IsSettings => Section == ShellSection.Settings;

    [ObservableProperty] private string _runtimeDescription = string.Empty;

    public void Dispose()
    {
        Home.Dispose();
        Settings.Dispose();
        Sources.Dispose();
        Installed.Dispose();
        Launcher.Dispose();
        Catalog.Dispose();
    }
}
