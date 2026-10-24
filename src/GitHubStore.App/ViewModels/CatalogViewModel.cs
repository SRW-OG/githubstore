using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitHubStore.App.Services.Localization;
using GitHubStore.Core.Abstractions;
using GitHubStore.Core.Catalog;
using System.Runtime.InteropServices;
using CatalogApp = GitHubStore.Core.Abstractions.App;
using SortKey = GitHubStore.Core.Abstractions.SortKey;

namespace GitHubStore.App.ViewModels;

/// <summary>由 UI 线程调用；目录服务独立于 WinUI，过期请求不能覆盖当前状态。</summary>
public sealed partial class CatalogViewModel : ObservableObject, IDisposable
{
    private readonly ICatalogService _catalog;
    private readonly ISearchService _search;
    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _detailCancellation;
    private readonly CancellationTokenSource _lifetime = new();
    private int _loadVersion;
    private int _total;
    private bool _disposed;
    private bool _ranking;
    private bool _searchResults;

    public CatalogViewModel(ICatalogService catalog, ISearchService search, LocalizationService? text = null,
        DownloadsViewModel? downloads = null, GitHubStore.App.Services.Ai.IReadmeSummaryService? readmeSummary = null,
        InstallationViewModel? installation = null, GitHubStore.App.Services.Ai.IAiAssistantService? assistant = null,
        GitHubStore.App.Services.Ai.IChineseReadmeService? chineseReadme = null)
    {
        _catalog = catalog;
        _chineseReadme = chineseReadme;
        _assistant = assistant;
        _readmeSummary = readmeSummary;
        _search = search;
        Downloads = downloads;
        Installation = installation;
        if (Installation is not null) Installation.PropertyChanged += OnInstallationChanged;
        Text = text ?? new LocalizationService();
        if (text is null) Text.ApplyLanguage("zh-CN");
        Text.LanguageChanged += OnLanguageChanged;
        StatusMessage = Text["PreparingCatalog"];
        Title = Text["DiscoverTitle"];
    }

    public LocalizationService Text { get; }
    public DownloadsViewModel? Downloads { get; }
    public InstallationViewModel? Installation { get; }
    public bool CanInstallSelected => Installation is not null && SelectedAsset is not null && !Installation.IsInstalling;
    private void OnInstallationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(InstallationViewModel.IsInstalling)) OnPropertyChanged(nameof(CanInstallSelected));
    }
    public AppInstallRequest? CreateInstallRequest() => SelectedDetail is { } detail && LatestRelease is { } release
        && SelectedAsset is { } selected ? new AppInstallRequest { App = detail.App, Release = release, Asset = selected.Asset, PackagePolicy = detail.PackagePolicy } : null;
    public IRepositoryCatalogService? Repositories => _catalog as IRepositoryCatalogService;
    public ObservableCollection<CatalogApp> Apps { get; } = [];
    public ObservableCollection<Category> Categories { get; } = [];
    public ObservableCollection<ReleaseAssetViewModel> WindowsAssets { get; } = [];
    public ObservableCollection<RecommendationSourceViewModel> RecommendationSources { get; } = [];
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedAsset), nameof(CanInstallSelected))]
    private ReleaseAssetViewModel? _selectedAsset;
    [ObservableProperty] private string _assetRecommendation = string.Empty;
    public bool HasSelectedAsset => SelectedAsset is not null;
    public Arch SystemArchitecture { get; } = RuntimeInformation.OSArchitecture switch
    {
        Architecture.Arm64 => Arch.Arm64,
        Architecture.X86 => Arch.X86,
        Architecture.X64 => Arch.Amd64,
        _ => Arch.Unknown,
    };
    [ObservableProperty] private Category? _selectedCategory;
    [ObservableProperty] private SortKey _sort = SortKey.Stars;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWorking))]
    private bool _isBusy;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWorking))]
    private bool _isRefreshing;
    [ObservableProperty] private bool _hasMore;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private CatalogApp? _selectedApp;
    // 扁平属性避免 XAML 深层绑定触发 AppDetail 的 required 成员激活。
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LatestRelease), nameof(ReleaseNotes), nameof(RepositoryUri), nameof(ReleasesUri),
        nameof(SnapshotText), nameof(CacheStatusText), nameof(HasDetail), nameof(HasNoDetail), nameof(HasNoRelease),
        nameof(HasNoWindowsAssets), nameof(DetailTitle), nameof(DetailDescription), nameof(DetailOwner),
        nameof(DetailRepo), nameof(DetailStars), nameof(DetailForks), nameof(DetailLicense), nameof(DetailLanguage),
        nameof(LatestReleaseTag), nameof(HasSummary), nameof(HasNoSummary), nameof(SummaryOneLiner), nameof(SummaryFeatures))]
    private AppDetail? _selectedDetail;
    [ObservableProperty] private string _title = string.Empty;

    public bool IsWorking => IsBusy || IsRefreshing;
    public bool HasDetail => SelectedDetail is not null;
    public string DetailTitle => SelectedDetail?.App.DisplayName ?? string.Empty;
    public string DetailDescription => SelectedDetail?.App.Description ?? string.Empty;
    public string DetailOwner => SelectedDetail?.App.Owner ?? string.Empty;
    public string DetailRepo => SelectedDetail?.App.Repo ?? string.Empty;
    public string DetailStars => SelectedDetail?.App.Stars.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
    public string DetailForks => SelectedDetail?.App.Forks.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
    public string DetailLicense => SelectedDetail?.App.License ?? string.Empty;
    public string DetailLanguage => SelectedDetail?.App.Language ?? string.Empty;
    public string LatestReleaseTag => LatestRelease?.TagName ?? string.Empty;

    // 摘要优先：有 AI 摘要时不重复展示长发行说明。
    public bool HasSummary => SelectedDetail?.Summary is not null;
    public bool HasNoSummary => !HasSummary;
    public string SummaryOneLiner => SelectedDetail?.Summary?.OneLiner ?? string.Empty;
    public IReadOnlyList<string> SummaryFeatures => SelectedDetail?.Summary?.KeyFeatures ?? [];
    public bool HasNoDetail => !HasDetail;
    public bool HasNoRelease => SelectedDetail is not null && LatestRelease is null;
    public bool HasNoWindowsAssets => SelectedDetail is not null && WindowsAssets.Count == 0;
    public Release? LatestRelease => SelectedDetail is { } detail ? DefaultAssetSelectionPolicy.SelectRelease(detail) : null;
    public string ReleaseNotes => LatestRelease?.Body is { } body
        ? (body.Length > 64000 ? body[..64000] + "\n\n…" : body) : string.Empty;
    public Uri? RepositoryUri => SelectedDetail is { } detail ? RepositoryLink(detail.App, false) : null;
    public Uri? ReleasesUri => SelectedDetail is { } detail ? RepositoryLink(detail.App, true) : null;
    public string SnapshotText => SelectedDetail?.App.FetchedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "—";
    public string CacheStatusText => Text[SelectedDetail?.App.Stale == true ? "CacheNotice" : "LiveNotice"];
    public string CurationDescription => SelectedDetail?.DescriptionOverride?.Trim()
        ?? (Text.IsEnglish ? string.Empty : SelectedDetail?.Curation?.DescriptionZh?.Trim() ?? string.Empty);
    public bool HasCurationDescription => !string.IsNullOrWhiteSpace(CurationDescription);
    public bool HasRecommendationSources => RecommendationSources.Count > 0;
    public bool HasVerification => SelectedDetail?.Curation?.Verification is not null;
    public string VerificationDateText => SelectedDetail?.Curation?.Verification is { } verification
        ? Text.Format("VerificationDate", verification.VerifiedAt.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
        : string.Empty;
    public string VerificationScopeText => SelectedDetail?.Curation?.Verification is { } verification
        ? Text[verification.Scope == "repository-license-windows-metadata" ? "VerificationMetadataScope" : "VerificationUnknownScope"]
        : string.Empty;
    public IReadOnlyList<string> VerificationNotes => SelectedDetail?.Curation?.Verification?.Notes
        .Where(note => !string.IsNullOrWhiteSpace(note)).Take(3)
        .Select(note => note.Length > 240 ? note[..240] + "…" : note).ToArray() ?? [];

    partial void OnSelectedDetailChanged(AppDetail? value)
    {
        ResetChineseReadmeState();
        ResetAiState();
        ResetAssistantDetail();
        SelectedAsset = null;
        WindowsAssets.Clear();
        Asset? recommended = null;
        if (value is not null && DefaultAssetSelectionPolicy.SelectRelease(value) is { } release)
        {
            recommended = DefaultAssetSelectionPolicy.Select(value, SystemArchitecture);
            foreach (var asset in release.Assets.Where(a => a.Kind is AssetKind.InstallerExe or AssetKind.InstallerMsi
                or AssetKind.PortableExe or AssetKind.ArchiveZip or AssetKind.Archive7z))
                WindowsAssets.Add(new ReleaseAssetViewModel(asset, Text, ReferenceEquals(asset, recommended)));
            SelectedAsset = WindowsAssets.FirstOrDefault(a => ReferenceEquals(a.Asset, recommended));
        }
        AssetRecommendation = recommended is null ? Text[value?.PackagePolicy is null ? "AssetNoRecommendation" : "AssetPolicyNoMatch"]
            : Text.Format("AssetRecommendedName", recommended.Name);
        OnPropertyChanged(nameof(HasNoWindowsAssets));
        RecommendationSources.Clear();
        if (value?.Curation is { } curation)
            foreach (var source in curation.RecommendationSources.Where(source => IsSafeSourceUri(source.Url)))
                RecommendationSources.Add(new RecommendationSourceViewModel(
                    $"{source.Platform} · {source.Title}", source.Url, Text[SourceEvidenceKey(source.EvidenceLevel)]));
        OnPropertyChanged(nameof(CurationDescription));
        OnPropertyChanged(nameof(HasCurationDescription));
        OnPropertyChanged(nameof(HasRecommendationSources));
        OnPropertyChanged(nameof(HasVerification));
        OnPropertyChanged(nameof(VerificationDateText));
        OnPropertyChanged(nameof(VerificationScopeText));
        OnPropertyChanged(nameof(VerificationNotes));
    }

    // 缓存也可能被手动修改；UI 不为非 HTTPS 或带凭据的来源生成可点击链接。
    private static bool IsSafeSourceUri(Uri? uri) => uri is { IsAbsoluteUri: true }
        && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo);

    private static string SourceEvidenceKey(string evidenceLevel) => evidenceLevel switch
    {
        "full-text" => "EvidenceFullText",
        // 知乎条目来自搜索索引摘要，小红书/B站条目来自接口返回的正文或视频简介。
        "indexed-excerpt" => "EvidenceSearchSnippet",
        "platform-text" => "EvidencePlatformText",
        "search-snippet" => "EvidenceSearchSnippet",
        "title-only" => "EvidenceTitleOnly",
        _ => "EvidenceUnknown"
    };

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            if (Repositories is { } repositories) await repositories.InitializeAsync(ct);
            // 队列必须在界面显示之前加载：中断的任务要对账为暂停状态，而不是等用户点开才出现。
            if (Downloads is { } downloads) await downloads.InitializeQueueAsync(ct);
            var categories = await _catalog.ListCategoriesAsync(ct);
            Categories.Clear();
            foreach (var category in categories) Categories.Add(category);
            await LoadAsync(ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ErrorMessage = FormatError("LoadFailed", ex); }
    }

    public async Task ShowHomeAsync(CancellationToken ct = default)
    {
        _ranking = false;
        SelectedCategory = null;
        SearchText = string.Empty;
        UpdateTitle();
        await LoadAsync(ct);
    }

    public async Task ShowCategoryAsync(Category category, CancellationToken ct = default)
    {
        _ranking = false;
        SelectedCategory = category;
        SearchText = string.Empty;
        UpdateTitle();
        await LoadAsync(ct);
    }

    public async Task ShowRankingAsync(CancellationToken ct = default)
    {
        _ranking = true;
        SelectedCategory = null;
        SearchText = string.Empty;
        UpdateTitle();
        await LoadAsync(ct);
    }

    public Task LoadAsync(CancellationToken ct = default) => LoadPageAsync(false, ct);

    private async Task LoadPageAsync(bool append, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CancelCards();
        ResetAssistantSearch();
        var previousLoad = _loadCancellation;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        _loadCancellation = cancellation;
        var version = ++_loadVersion;
        var token = cancellation.Token;
        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            if (previousLoad is not null) await previousLoad.CancelAsync();
            IReadOnlyList<CatalogApp> items;
            int total;
            bool hasMore;
            _searchResults = !string.IsNullOrWhiteSpace(SearchText);
            if (_searchResults)
            {
                var result = await _search.SearchAsync(SearchText.Trim(), new SearchOptions
                {
                    Category = SelectedCategory?.Id,
                    Limit = 500,
                    IncludeRemote = false
                }, token);
                items = result.Items;
                total = result.Total;
                hasMore = false;
            }
            else if (_ranking)
            {
                items = await _catalog.RankAsync(BoardKind.Stars, 60, token);
                total = items.Count;
                hasMore = false;
            }
            else
            {
                var result = await _catalog.BrowseAsync(new CatalogQuery
                {
                    Category = SelectedCategory?.Id,
                    Sort = Sort,
                    Offset = append ? Apps.Count : 0,
                    Limit = 12
                }, token);
                items = result.Items;
                total = result.Total;
                hasMore = result.HasMore;
            }
            if (version != _loadVersion || token.IsCancellationRequested) return;
            if (!append) Apps.Clear();
            foreach (var item in items) Apps.Add(item);
            _total = total;
            HasMore = hasMore;
            UpdateStatus();
            await RefreshCardsAsync(token);
            if (version != _loadVersion || token.IsCancellationRequested) return;
            if (!append) await SelectAsync(Apps.FirstOrDefault(), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (version == _loadVersion)
            {
                ErrorMessage = FormatError("LoadFailed", ex);
                StatusMessage = Text["RetryHint"];
                HasMore = false;
            }
        }
        finally
        {
            if (ReferenceEquals(_loadCancellation, cancellation)) _loadCancellation = null;
            if (version == _loadVersion) IsBusy = false;
        }
    }

    public async Task SelectAsync(CatalogApp? app, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var previousDetail = _detailCancellation;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        _detailCancellation = cancellation;
        SelectedDetail = null;
        try
        {
            if (previousDetail is not null) await previousDetail.CancelAsync();
            if (app is null) return;
            var detail = await _catalog.GetAppAsync(app.Id, cancellation.Token);
            if (!cancellation.IsCancellationRequested)
            {
                SelectedDetail = detail;
                await RestoreSummaryAsync(new AppDetailIdentity(detail.App.Id), cancellation.Token);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!cancellation.IsCancellationRequested) ErrorMessage = FormatError("DetailFailed", ex);
        }
        finally
        {
            if (ReferenceEquals(_detailCancellation, cancellation)) _detailCancellation = null;
        }
    }

    public async Task ShowAddedAsync(AppDetail detail, CancellationToken ct = default)
    {
        await ShowHomeAsync(ct);
        if (!_disposed) await SelectAsync(detail.App, ct);
    }

    public async Task RemoveSelectedAsync(CancellationToken ct = default)
    {
        if (Repositories is not { } repositories || SelectedDetail is not { } selected) return;
        try
        {
            await repositories.RemoveAsync(selected.App.Id, ct);
            await LoadAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { ErrorMessage = FormatError("RemoveFailed", ex); }
    }

    [RelayCommand]
    private Task SearchAsync(CancellationToken ct) => LoadAsync(ct);

    [RelayCommand]
    private Task LoadMoreAsync(CancellationToken ct) => HasMore && !IsWorking && !_searchResults
        ? LoadPageAsync(true, ct) : Task.CompletedTask;

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken ct)
    {
        if (_disposed) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        var token = cancellation.Token;
        IsRefreshing = true;
        ErrorMessage = string.Empty;
        try
        {
            var progress = new Progress<RefreshProgress>(p =>
            {
                if (!_disposed && IsRefreshing) StatusMessage = $"{Text["Refresh"]} {p.Done} / {p.Total}";
            });
            await _catalog.RefreshAsync(RefreshScope.All, progress, token);
            if (!_disposed) await LoadAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_disposed) ErrorMessage = FormatError("RefreshFailed", ex);
        }
        finally { IsRefreshing = false; }
    }

    public string FormatError(string prefixKey, Exception exception)
    {
        var reason = exception is RepositoryCatalogException repositoryError ? Text[repositoryError.Code switch
        {
            "invalid_repository" => "InvalidRepository",
            "invalid_category" => "InvalidCategory",
            "duplicate_repository" => "DuplicateRepository",
            "repository_not_found" => "RepositoryNotFound",
            "repository_not_public" => "RepositoryNotPublic",
            "rate_limited" => "RateLimited",
            "access_denied" => "AccessDenied",
            "request_timeout" => "RequestTimeout",
            "network_error" => "NetworkError",
            "invalid_response" => "InvalidResponse",
            "invalid_package_policy" => "InvalidPackagePolicy",
            "cache_write_failed" => "CacheWriteFailed",
            "cache_read_failed" => "CacheReadFailed",
            "seed_unavailable" => "InvalidResponse",
            _ => "ActionFailed"
        }] : exception.Message;
        // 前缀词条含 {0} 占位符，必须走格式化而不是字符串插值。
        var template = Text[prefixKey];
        return template.Contains("{0}", StringComparison.Ordinal)
            ? string.Format(CultureInfo.CurrentCulture, template, reason)
            : $"{template}：{reason}";
    }

    private static Uri RepositoryLink(CatalogApp app, bool releases) =>
        new($"https://github.com/{Uri.EscapeDataString(app.Owner)}/{Uri.EscapeDataString(app.Repo)}{(releases ? "/releases" : string.Empty)}");

    private void OnLanguageChanged(object? sender, EventArgs args)
    {
        ResetAssistantSearch();
        NotifyAssistantLabels();
        NotifyChineseReadmeLabels();
        _ = RefreshCardsAsync(_lifetime.Token);
        UpdateTitle();
        UpdateStatus();
        OnPropertyChanged(nameof(CacheStatusText));
        var selectedId = SelectedAsset?.Asset.InstallerId;
        OnSelectedDetailChanged(SelectedDetail);
        if (selectedId is not null)
            SelectedAsset = WindowsAssets.FirstOrDefault(a => a.Asset.InstallerId == selectedId);
    }

    private void UpdateTitle() => Title = SelectedCategory is { } category
        ? Text[$"category.{category.Id.Value}"] : Text[_ranking ? "RankingsTitle" : "DiscoverTitle"];

    private void UpdateStatus() => StatusMessage = Apps.Count == 0 ? Text["EmptyCatalog"]
        : string.Format(CultureInfo.CurrentCulture, Text["ShowingCount"], Apps.Count, _total);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelCards();
        CancelChineseReadme();
        ResetAssistantSearch();
        ResetAssistantDetail();
        Text.LanguageChanged -= OnLanguageChanged;
        Downloads?.Dispose();
        if (Installation is not null)
        {
            Installation.PropertyChanged -= OnInstallationChanged;
            Installation.Dispose();
        }
        _lifetime.Cancel();
        _loadCancellation?.Cancel();
        _detailCancellation?.Cancel();
        _aiCancellation?.Cancel();
        _lifetime.Dispose();
    }
}

public sealed class RecommendationSourceViewModel(string linkText, Uri url, string evidenceText)
{
    public string LinkText { get; } = linkText;
    public Uri Url { get; } = url;
    public string EvidenceText { get; } = evidenceText;
}

public sealed class ReleaseAssetViewModel(Asset asset, LocalizationService? text = null, bool isRecommended = false)
{
    internal Asset Asset => asset;
    public bool IsRecommended => isRecommended;
    public string SelectionLabel => $"{(isRecommended ? (text?["AssetRecommendedBadge"] ?? "Recommended") + " · " : string.Empty)}{Name} · {Architecture} · {Size}";
    public string DownloadLabel => text?["DownloadAsset"] ?? "Download";
    public string Name => asset.Name;
    public string Architecture => DefaultAssetSelectionPolicy.InferArchitecture(asset) switch { Arch.Amd64 => "x64", Arch.Arm64 => "ARM64", Arch.X86 => "x86", _ => "—" };
    public string Size => $"{asset.Size / (1024d * 1024d):N1} MiB";
    public string Kind => asset.Kind switch
    {
        AssetKind.InstallerMsi => "MSI",
        AssetKind.ArchiveZip => "ZIP",
        AssetKind.Archive7z => "7z",
        _ => "EXE",
    };
}
