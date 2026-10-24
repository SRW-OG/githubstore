using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitHubStore.App.Services.Ai;
using GitHubStore.App.Services.Localization;
using GitHubStore.Core.Abstractions;
using GitHubStore.Core.Catalog;

namespace GitHubStore.App.ViewModels;

public sealed record WizardChannelChoice(PackageReleaseChannel Value, string Name);
public sealed record WizardFormatChoice(PackageInstallFormat Value, string Name);
public sealed class WizardAssetChoice(Asset asset)
{
    internal Asset Asset => asset;
    public string Name => asset.Name;
    public string InstallerId => asset.InstallerId;
}

public sealed partial class AddRepositoryViewModel : ObservableObject, IDisposable
{
    private readonly IRepositoryCatalogService _repositories;
    private readonly IRepositorySuggestionService? _suggestions;
    private readonly CatalogViewModel _catalog;
    private CancellationTokenSource _session = new();
    private int _version, _edits, _operation;
    private string? _previewedInput;
    private bool _disposed;

    public AddRepositoryViewModel(IRepositoryCatalogService repositories, CatalogViewModel catalog, IRepositorySuggestionService? suggestions = null)
    {
        _repositories = repositories; _catalog = catalog; _suggestions = suggestions; Text = catalog.Text;
        RefreshCategories();
    }
    public void RefreshCategories()
    {
        var previous = SelectedCategory?.Id;
        Categories.Clear();
        foreach (var category in _catalog.Categories) Categories.Add(category with { Name = Text[$"category.{category.Id.Value}"] });
        SelectedCategory = Categories.FirstOrDefault(c => previous is { } id && c.Id == id)
            ?? Categories.FirstOrDefault(c => c.Id.Value == "system") ?? Categories.FirstOrDefault();
        Channels = Enum.GetValues<PackageReleaseChannel>().Select(v => new WizardChannelChoice(v, Text[$"WizardChannel{v}"])).ToArray();
        Formats = Enum.GetValues<PackageInstallFormat>().Select(v => new WizardFormatChoice(v, Text[$"WizardFormat{v}"])).ToArray();
        OnPropertyChanged(nameof(Channels)); OnPropertyChanged(nameof(Formats));
        SelectedChannel = Channels[0]; SelectedFormat = Formats[0];
    }
    public void BeginDialog()
    {
        EndDialog(); _session.Dispose(); _session = new(); IsBusy = false;
        RefreshCategories(); RepositoryInput = string.Empty; PinnedTag = string.Empty;
        ExecutableRelativePath = string.Empty; Description = string.Empty; InvalidatePreview();
    }
    public void EndDialog()
    {
        _operation++; InvalidatePreview();
        try { _session.Cancel(); }
        catch (AggregateException ex) { System.Diagnostics.Trace.WriteLine(ex); }
    }
    public LocalizationService Text { get; }
    public ObservableCollection<Category> Categories { get; } = [];
    public ObservableCollection<WizardAssetChoice> Assets { get; } = [];
    public IReadOnlyList<WizardChannelChoice> Channels { get; private set; } = [];
    public IReadOnlyList<WizardFormatChoice> Formats { get; private set; } = [];
    [ObservableProperty] private bool _catalogOnly;
    partial void OnCatalogOnlyChanged(bool value) => Edited();
    public bool CanChooseCatalogOnly => HasPreview && Assets.Count == 0;
    public bool HasEligibleAssets => HasPreview && Assets.Count > 0;
    [ObservableProperty] private string _repositoryInput = string.Empty;
    [ObservableProperty] private Category? _selectedCategory;
    [ObservableProperty] private WizardChannelChoice? _selectedChannel;
    [ObservableProperty] private WizardFormatChoice? _selectedFormat;
    [ObservableProperty] private string _pinnedTag = string.Empty;
    [ObservableProperty] private WizardAssetChoice? _selectedAsset;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private string _filenamePattern = string.Empty;
    [ObservableProperty] private string _executableRelativePath = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdd), nameof(HasPreview), nameof(CanSuggest), nameof(PreviewTitle), nameof(PreviewDescription), nameof(PreviewOwner), nameof(PreviewRepo), nameof(PreviewStars), nameof(PreviewLicense), nameof(ReleaseTag))]
    private AppDetail? _previewDetail;
    public bool HasPreview => PreviewDetail is not null;
    public string PreviewTitle => PreviewDetail?.App.DisplayName ?? string.Empty;
    public string PreviewDescription => PreviewDetail?.App.Description ?? string.Empty;
    public string PreviewOwner => PreviewDetail?.App.Owner ?? string.Empty;
    public string PreviewRepo => PreviewDetail?.App.Repo ?? string.Empty;
    public string PreviewStars => PreviewDetail?.App.Stars.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    public string PreviewLicense => PreviewDetail?.App.License ?? string.Empty;
    public string ReleaseTag => PreviewDetail?.Releases.FirstOrDefault()?.TagName ?? string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdd), nameof(IsEditable), nameof(CanSuggest), nameof(CanApplySuggestion))]
    private bool _isBusy;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSuggestion), nameof(SuggestionText), nameof(CanApplySuggestion))]
    private RepositorySuggestion? _suggestion;
    public bool HasSuggestion => Suggestion is not null;
    public string SuggestionText => Suggestion is { } s ? $"{Assets.FirstOrDefault(a => a.InstallerId == s.AssetId)?.Name}\n{Categories.FirstOrDefault(c => c.Id.Value == s.CategoryId)?.Name}\n{s.Description}" : string.Empty;
    public bool CanSuggest => !_disposed && !IsBusy && HasEligibleAssets && _suggestions?.IsConfigured == true;
    public bool CanApplySuggestion => !IsBusy && HasSuggestion;
    public bool IsEditable => !IsBusy;
    public bool CanAdd => !_disposed && !_session.IsCancellationRequested && !IsBusy && HasPreview
        && SelectedCategory is not null && (CanChooseCatalogOnly && CatalogOnly
            || HasEligibleAssets && SelectedAsset is not null && !string.IsNullOrWhiteSpace(FilenamePattern))
        && string.Equals(_previewedInput, RepositoryInput.Trim(), StringComparison.Ordinal);
    partial void OnRepositoryInputChanged(string value) => InvalidatePreview();
    partial void OnSelectedChannelChanged(WizardChannelChoice? value) => InvalidatePreview();
    partial void OnPinnedTagChanged(string value) => InvalidatePreview();
    partial void OnSelectedCategoryChanged(Category? value) => Edited();
    partial void OnDescriptionChanged(string value) => Edited();
    partial void OnSelectedFormatChanged(WizardFormatChoice? value) => Edited();
    partial void OnFilenamePatternChanged(string value) => Edited();
    partial void OnExecutableRelativePathChanged(string value) => Edited();
    partial void OnSelectedAssetChanged(WizardAssetChoice? value)
    {
        Edited();
        var pattern = value?.Name ?? string.Empty;
        var tag = ReleaseTag;
        if (!string.IsNullOrEmpty(tag))
        {
            pattern = pattern.Replace(tag, "*", StringComparison.OrdinalIgnoreCase);
            if (tag.StartsWith('v') && tag.Length > 1) pattern = pattern.Replace(tag[1..], "*", StringComparison.OrdinalIgnoreCase);
        }
        FilenamePattern = Regex.Replace(pattern, @"(?<!\d)\d+\.\d+(?:\.\d+)*(?:[-+]\d+)?", "*");
    }
    private void Edited() { _edits++; Suggestion = null; OnPropertyChanged(nameof(CanAdd)); }
    private void InvalidatePreview()
    {
        _version++; PreviewDetail = null; _previewedInput = null; Assets.Clear(); SelectedAsset = null;
        Suggestion = null; CatalogOnly = false; ErrorMessage = string.Empty;
        OnPropertyChanged(nameof(CanAdd)); OnPropertyChanged(nameof(CanChooseCatalogOnly)); OnPropertyChanged(nameof(HasEligibleAssets));
    }
    private PackagePolicy Policy(bool selection) => new()
    {
        ReleaseChannel = SelectedChannel?.Value ?? PackageReleaseChannel.Stable,
        PinnedTag = SelectedChannel?.Value == PackageReleaseChannel.Pinned ? PinnedTag.Trim() : null,
        InstallFormat = selection ? SelectedFormat?.Value ?? PackageInstallFormat.Auto : PackageInstallFormat.Auto,
        AssetRules = selection && SelectedAsset is { } asset ? [new PackageAssetRule { Architecture = DefaultAssetSelectionPolicy.InferArchitecture(asset.Asset), Include = [FilenamePattern.Trim()] }] : [],
        ExecutableRelativePath = selection && !string.IsNullOrWhiteSpace(ExecutableRelativePath) ? ExecutableRelativePath.Trim() : null,
    };
    private static Arch CurrentArchitecture => System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.X64 => Arch.Amd64,
        System.Runtime.InteropServices.Architecture.Arm64 => Arch.Arm64,
        System.Runtime.InteropServices.Architecture.X86 => Arch.X86,
        _ => Arch.Unknown,
    };
    private static bool SafeName(string name)
    {
        try { PackagePolicyValidator.ValidateRelativePath(name); return name.Length <= 255 && !name.Contains('/') && !name.Contains('\\'); }
        catch (RepositoryCatalogException) { return false; }
    }
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task PreviewAsync(CancellationToken ct)
    {
        if (_disposed || _session.IsCancellationRequested || IsBusy) return;
        var input = RepositoryInput.Trim(); InvalidatePreview(); var version = _version; var operation = ++_operation;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _session.Token);
        IsBusy = true;
        try
        {
            var policy = Policy(false); PackagePolicyValidator.Validate(policy);
            var detail = _repositories is IPackagePolicyCatalogService configured ? await configured.PreviewAsync(input, policy, cancellation.Token)
                : await _repositories.PreviewAsync(input, cancellation.Token);
            if (_disposed || cancellation.IsCancellationRequested || version != _version) return;
            _previewedInput = input; PreviewDetail = detail; Description = detail.App.Description ?? string.Empty;
            foreach (var asset in DefaultAssetSelectionPolicy.GetEligibleAssets(detail.Releases.SelectMany(r => r.Assets), CurrentArchitecture,
                $"{detail.App.Owner}/{detail.App.Repo}", policy with { AssetRules = [new PackageAssetRule { Architecture = Arch.Unknown, Include = ["*"] }] })
                .Where(a => SafeName(a.Name))) Assets.Add(new WizardAssetChoice(asset));
            OnPropertyChanged(nameof(CanChooseCatalogOnly)); OnPropertyChanged(nameof(HasEligibleAssets)); OnPropertyChanged(nameof(CanSuggest));
            if (Assets.Count == 0) ErrorMessage = Text["WizardCatalogOnlyHint"];
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) { if (!_disposed && version == _version) ErrorMessage = _catalog.FormatError("AddFailed", ex); }
        finally { if (!_disposed && operation == _operation) IsBusy = false; }
    }
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SuggestAsync(CancellationToken ct)
    {
        if (!CanSuggest || _suggestions is null || PreviewDetail is not { } detail) return;
        var version = _version; var edits = _edits; var operation = ++_operation;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _session.Token);
        IsBusy = true; ErrorMessage = string.Empty;
        try
        {
            var eligibleIds = Assets.Select(a => a.InstallerId).ToHashSet(StringComparer.Ordinal);
            var suggestionInput = detail with { Releases = detail.Releases.Select(r => r with { Assets = r.Assets.Where(a => eligibleIds.Contains(a.InstallerId)).ToArray() }).ToArray() };
            var suggestion = await _suggestions.SuggestAsync(suggestionInput, Categories.ToArray(), Text.LanguageCode, cancellation.Token);
            if (!_disposed && !cancellation.IsCancellationRequested && version == _version && edits == _edits)
            {
                if (!Assets.Any(a => a.InstallerId == suggestion.AssetId) || !Categories.Any(c => c.Id.Value == suggestion.CategoryId)) throw new AiSummaryException("ai_invalid_response");
                Suggestion = suggestion;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception) { if (!_disposed && version == _version && edits == _edits) ErrorMessage = Text["WizardAiFailed"]; }
        finally { if (!_disposed && operation == _operation) IsBusy = false; }
    }
    [RelayCommand]
    private void ApplySuggestion()
    {
        if (!CanApplySuggestion || Suggestion is not { } suggestion) return;
        SelectedAsset = Assets.FirstOrDefault(a => a.InstallerId == suggestion.AssetId);
        SelectedCategory = Categories.FirstOrDefault(c => c.Id.Value == suggestion.CategoryId);
        Description = suggestion.Description; Suggestion = null;
    }
    public async Task<AppDetail?> AddAsync(CancellationToken ct = default)
    {
        if (!CanAdd) { ErrorMessage = Text["WizardReviewFirst"]; return null; }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _session.Token);
        var operation = ++_operation; IsBusy = true; ErrorMessage = string.Empty;
        try
        {
            if (_repositories is not IPackagePolicyCatalogService configured) throw new RepositoryCatalogException("invalid_package_policy");
            if (CanChooseCatalogOnly && CatalogOnly)
                return await configured.AddCatalogOnlyAsync(RepositoryInput.Trim(), SelectedCategory?.Id, Policy(false), Description.Trim(),
                    PreviewDetail?.Releases.FirstOrDefault()?.TagName, cancellation.Token);
            return await configured.AddAsync(RepositoryInput.Trim(), SelectedCategory?.Id, Policy(true), Description.Trim(), SelectedAsset!.Name, ReleaseTag, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return null; }
        catch (Exception ex)
        {
            if (!_disposed && !cancellation.IsCancellationRequested) ErrorMessage = ex is RepositoryCatalogException { Code: "selected_asset_unavailable" or "invalid_asset_name" or "invalid_description" }
                ? Text["WizardSelectionChanged"] : _catalog.FormatError("AddFailed", ex);
            return null;
        }
        finally { if (!_disposed && operation == _operation) IsBusy = false; }
    }
    public void Dispose() { if (_disposed) return; EndDialog(); _disposed = true; _session.Dispose(); }
}
