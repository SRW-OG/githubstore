using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using GitHubStore.Core.Abstractions;
using CatalogApp = GitHubStore.Core.Abstractions.App;

namespace GitHubStore.App.ViewModels;

/// <summary>A local catalog snapshot. README image discovery is explicit and never generates AI content.</summary>
public sealed class CatalogCardViewModel : ObservableObject, IDisposable
{
    private readonly bool _english;
    private readonly bool _descriptionIsSource;
    private readonly Func<CancellationToken, Task<Uri?>>? _screenshotLoader;
    private readonly CancellationToken _scopeToken;
    private CancellationTokenSource? _screenshotCancellation;
    private Task? _screenshotTask;
    private Uri? _screenshotUri;
    private bool _screenshotAttempted;
    private bool _isScreenshotLoading;
    private bool _disposed;
    private int _screenshotVersion;

    public CatalogCardViewModel(AppDetail detail, bool english = false,
        Func<CancellationToken, Task<Uri?>>? screenshotLoader = null, CancellationToken scopeToken = default)
    {
        App = detail.App;
        _english = english;
        _screenshotLoader = screenshotLoader;
        _scopeToken = scopeToken;
        RepositoryUri = SafeRepositoryUri(App);
        var original = App.Description?.Trim() ?? string.Empty;
        var curated = !english ? detail.Curation?.DescriptionZh?.Trim() ?? string.Empty : string.Empty;
        var features = detail.Summary?.KeyFeatures.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim()).Distinct(StringComparer.Ordinal).Take(6).ToArray() ?? [];
        if (features.Length > 0)
        {
            Description = curated.Length > 0 ? curated : original;
            FeaturesText = string.Join("\n", features.Select(value => "• " + value));
        }
        else
        {
            // Curated introductions and repository descriptions already contain actual functional facts.
            // Display each paragraph once rather than claim those facts are unavailable.
            var functionalDescription = curated.Length > 0 ? curated : original;
            Description = original.Length > 0 && !string.Equals(original, functionalDescription, StringComparison.Ordinal)
                ? original : detail.Summary?.OneLiner?.Trim() ?? string.Empty;
            if (Description.Length == 0 && functionalDescription.Length > 0)
            {
                Description = $"{App.DisplayName} · {App.Owner}/{App.Repo}";
                _descriptionIsSource = true;
            }
            var topics = App.Topics.Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToArray();
            FeaturesText = (functionalDescription.Length > 0 ? functionalDescription
                : Label("暂无独立功能清单。", "No separate feature list available."))
                + (topics.Length == 0 ? string.Empty : "\n" + Label("仓库标签：", "Repository topics: ") + string.Join(" · ", topics));
        }
        if (Description.Length == 0) Description = Label("暂无软件介绍。", "No application description available.");
        LatestVersion = detail.Releases.FirstOrDefault(release => !release.Draft)?.TagName
            ?? Label("暂无发行版本", "No published release");
        _screenshotUri = detail.Screenshots.Select(SafeImageUri).FirstOrDefault(uri => uri is not null);
    }

    public CatalogApp App { get; }
    public string DisplayName => App.DisplayName;
    public Uri? RepositoryUri { get; }
    public string RepositoryText => RepositoryUri?.AbsoluteUri ?? Label("仓库地址不可用", "Repository URL unavailable");
    public string Description { get; }
    public string FeaturesText { get; }
    public int StarCount => App.Stars;
    public string StarsText => $"★ {StarCount.ToString("N0", _english ? CultureInfo.GetCultureInfo("en-US") : CultureInfo.GetCultureInfo("zh-CN"))}";
    public DateTimeOffset UpdatedAt => App.PushedAt;
    public string UpdatedText => UpdatedAt == default ? Label("未知", "Unknown")
        : UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public string LatestVersion { get; }
    public string FeaturesLabel => Label("功能", "Features");
    public string UpdatedLabel => Label("最近代码更新", "Latest code update");
    public string VersionLabel => Label("最新版本", "Latest version");
    public string ScreenshotLabel => Label("软件截图", "Application screenshot");
    public string InstallLabel => Label("安装", "Install");
    public string MoreLabel => Label("更多", "More");
    public string DescriptionLabel => _descriptionIsSource ? Label("来源", "Source") : Label("软件介绍", "Description");
    public Uri? ScreenshotUri
    {
        get => _screenshotUri;
        private set
        {
            if (!SetProperty(ref _screenshotUri, value)) return;
            OnPropertyChanged(nameof(HasScreenshot));
            OnPropertyChanged(nameof(HasNoScreenshot));
            OnPropertyChanged(nameof(ScreenshotFallback));
        }
    }
    public bool HasScreenshot => ScreenshotUri is not null;
    public bool HasNoScreenshot => !HasScreenshot;
    public bool IsScreenshotLoading
    {
        get => _isScreenshotLoading;
        private set
        {
            if (SetProperty(ref _isScreenshotLoading, value)) OnPropertyChanged(nameof(ScreenshotFallback));
        }
    }
    public string ScreenshotFallback => HasScreenshot ? string.Empty
        : IsScreenshotLoading ? Label("正在加载截图…", "Loading screenshot…")
        : _screenshotAttempted || _screenshotLoader is null ? Label("暂无可用截图", "No screenshot available")
        : Label("截图将在显示时加载", "Screenshot loads when this card is visible");

    /// <summary>Call when a card is realized. Concurrent Loaded events share one bounded request.</summary>
    public Task LoadScreenshotAsync(CancellationToken ct = default)
    {
        if (_disposed || _scopeToken.IsCancellationRequested || ct.IsCancellationRequested || HasScreenshot
            || _screenshotAttempted || _screenshotLoader is null) return Task.CompletedTask;
        if (_screenshotTask is { IsCompleted: false }) return _screenshotTask;
        _screenshotTask = LoadScreenshotCoreAsync(ct);
        return _screenshotTask;
    }

    private async Task LoadScreenshotCoreAsync(CancellationToken ct)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _scopeToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(25));
        _screenshotCancellation = cancellation;
        var version = ++_screenshotVersion;
        IsScreenshotLoading = true;
        try
        {
            var uri = await _screenshotLoader!(cancellation.Token).WaitAsync(cancellation.Token);
            if (!IsCurrent()) return;
            _screenshotAttempted = true;
            ScreenshotUri = SafeImageUri(uri?.AbsoluteUri);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (IsCurrent(includeCancelled: true) && !ct.IsCancellationRequested && !_scopeToken.IsCancellationRequested)
                _screenshotAttempted = true; // A timed-out image stays a placeholder until the catalog refreshes.
        }
        catch (Exception)
        {
            if (IsCurrent()) _screenshotAttempted = true;
        }
        finally
        {
            if (IsCurrent(includeCancelled: true))
            {
                _screenshotCancellation = null;
                IsScreenshotLoading = false;
                OnPropertyChanged(nameof(ScreenshotFallback));
            }
        }
        bool IsCurrent(bool includeCancelled = false) => !_disposed && version == _screenshotVersion
            && ReferenceEquals(_screenshotCancellation, cancellation)
            && (includeCancelled || !cancellation.IsCancellationRequested);
    }

    /// <summary>ImageFailed must clear a broken image so the placeholder becomes visible.</summary>
    public void ClearScreenshot()
    {
        ++_screenshotVersion;
        var cancellation = _screenshotCancellation;
        _screenshotCancellation = null;
        if (cancellation is not null) _ = CancelSafelyAsync(cancellation);
        _screenshotAttempted = true;
        ScreenshotUri = null;
        IsScreenshotLoading = false;
        OnPropertyChanged(nameof(ScreenshotFallback));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ++_screenshotVersion;
        var cancellation = _screenshotCancellation;
        _screenshotCancellation = null;
        if (cancellation is not null) _ = CancelSafelyAsync(cancellation);
        IsScreenshotLoading = false;
    }

    private static async Task CancelSafelyAsync(CancellationTokenSource cancellation)
    {
        try { await cancellation.CancelAsync(); }
        catch (Exception) { /* No observer may block catalog navigation or disposal. */ }
    }
    private string Label(string chinese, string english) => _english ? english : chinese;
    private static Uri? SafeRepositoryUri(CatalogApp app)
    {
        var owner = app.Owner ?? string.Empty;
        var repo = app.Repo ?? string.Empty;
        return Regex.IsMatch(owner, "^[A-Za-z0-9-]+$") && Regex.IsMatch(repo, "^[A-Za-z0-9_.-]+$")
            && repo is not ("." or "..")
            ? new Uri($"https://github.com/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}") : null;
    }
    internal static Uri? SafeImageUri(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo) && uri.IsDefaultPort
        && !uri.IsLoopback && Uri.CheckHostName(uri.Host) == UriHostNameType.Dns ? uri : null;
}
