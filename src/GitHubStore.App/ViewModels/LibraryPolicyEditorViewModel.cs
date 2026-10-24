using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitHubStore.App.Services.Localization;
using GitHubStore.Core.Abstractions;
using GitHubStore.Core.Catalog;

namespace GitHubStore.App.ViewModels;

public sealed record LibraryChannelChoice(PackageReleaseChannel Value, string Name);
public sealed record LibraryFormatChoice(PackageInstallFormat Value, string Name);

/// <summary>Edits existing entries; previews are explicitly based on the local release snapshot.</summary>
public sealed partial class LibraryPolicyEditorViewModel : ObservableObject, IDisposable
{
    private readonly IPackagePolicyCatalogService _catalog;
    private readonly LocalizationService _text;
    private PackagePolicy? _previewedPolicy;
    private AppId? _previewedId;
    private bool _loading;
    private bool _disposed;
    private readonly CancellationTokenSource _lifetime = new();

    public LibraryPolicyEditorViewModel(IPackagePolicyCatalogService catalog, LocalizationService text)
    {
        _catalog = catalog;
        _text = text;
        RefreshChoices();
        _text.LanguageChanged += OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, EventArgs args) => RefreshChoices();

    private void RefreshChoices()
    {
        var channel = Channel?.Value;
        var format = Format?.Value;
        _loading = true;
        try
        {
            Channels = Enum.GetValues<PackageReleaseChannel>().Select(v => new LibraryChannelChoice(v, _text[$"WizardChannel{v}"])).ToArray();
            Formats = Enum.GetValues<PackageInstallFormat>().Select(v => new LibraryFormatChoice(v, _text[$"WizardFormat{v}"])).ToArray();
            OnPropertyChanged(nameof(Channels)); OnPropertyChanged(nameof(Formats));
            Channel = Channels.FirstOrDefault(c => c.Value == channel);
            Format = Formats.FirstOrDefault(c => c.Value == format);
        }
        finally { _loading = false; }
    }

    public LocalizationService Text => _text;
    public ObservableCollection<LibraryEntryChoice> Entries { get; } = [];
    public IReadOnlyList<LibraryChannelChoice> Channels { get; private set; } = [];
    public IReadOnlyList<LibraryFormatChoice> Formats { get; private set; } = [];
    [ObservableProperty] private LibraryEntryChoice? _selectedEntry;
    [ObservableProperty] private LibraryChannelChoice? _channel;
    [ObservableProperty] private LibraryFormatChoice? _format;
    [ObservableProperty] private string _pinnedTag = string.Empty;
    [ObservableProperty] private string _executablePath = string.Empty;
    [ObservableProperty] private string _preservePaths = string.Empty;
    [ObservableProperty] private bool _useX64Rule;
    [ObservableProperty] private bool _useArm64Rule;
    [ObservableProperty] private bool _useX86Rule;
    [ObservableProperty] private bool _useAnyRule;
    [ObservableProperty] private string _x64Include = string.Empty;
    [ObservableProperty] private string _x64Exclude = string.Empty;
    [ObservableProperty] private string _arm64Include = string.Empty;
    [ObservableProperty] private string _arm64Exclude = string.Empty;
    [ObservableProperty] private string _x86Include = string.Empty;
    [ObservableProperty] private string _x86Exclude = string.Empty;
    [ObservableProperty] private string _anyInclude = string.Empty;
    [ObservableProperty] private string _anyExclude = string.Empty;
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private string _preview = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit), nameof(CanSave))]
    private bool _isBusy;
    public bool CanEdit => !_disposed && !IsBusy && SelectedEntry is not null;
    public bool CanSave => CanEdit && _previewedPolicy is not null && _previewedId == SelectedEntry?.Detail.App.Id;

    public void SetEntries(IReadOnlyList<AppDetail> entries)
    {
        if (IsBusy) return;
        var selected = SelectedEntry?.Detail.App.Id;
        Entries.Clear();
        foreach (var detail in entries.OrderBy(e => e.App.Owner).ThenBy(e => e.App.Repo)) Entries.Add(new LibraryEntryChoice(detail));
        SelectedEntry = Entries.FirstOrDefault(e => e.Detail.App.Id == selected) ?? Entries.FirstOrDefault();
    }

    partial void OnSelectedEntryChanged(LibraryEntryChoice? value)
    {
        _loading = true;
        var policy = value?.Detail.PackagePolicy ?? new PackagePolicy();
        Channel = Channels.First(c => c.Value == policy.ReleaseChannel);
        Format = Formats.First(c => c.Value == policy.InstallFormat);
        PinnedTag = policy.PinnedTag ?? string.Empty;
        ExecutablePath = policy.ExecutableRelativePath ?? string.Empty;
        PreservePaths = string.Join(Environment.NewLine, policy.PreservePaths);
        UseX64Rule = policy.AssetRules.Any(r => r.Architecture == Arch.Amd64);
        UseArm64Rule = policy.AssetRules.Any(r => r.Architecture == Arch.Arm64);
        UseX86Rule = policy.AssetRules.Any(r => r.Architecture == Arch.X86);
        UseAnyRule = policy.AssetRules.Any(r => r.Architecture == Arch.Unknown);
        X64Include = Patterns(policy, Arch.Amd64, true); X64Exclude = Patterns(policy, Arch.Amd64, false);
        Arm64Include = Patterns(policy, Arch.Arm64, true); Arm64Exclude = Patterns(policy, Arch.Arm64, false);
        X86Include = Patterns(policy, Arch.X86, true); X86Exclude = Patterns(policy, Arch.X86, false);
        AnyInclude = Patterns(policy, Arch.Unknown, true); AnyExclude = Patterns(policy, Arch.Unknown, false);
        _loading = false;
        InvalidatePreview();
        OnPropertyChanged(nameof(CanEdit));
    }

    partial void OnChannelChanged(LibraryChannelChoice? value) => InvalidatePreview();
    partial void OnFormatChanged(LibraryFormatChoice? value) => InvalidatePreview();
    partial void OnPinnedTagChanged(string value) => InvalidatePreview();
    partial void OnExecutablePathChanged(string value) => InvalidatePreview();
    partial void OnPreservePathsChanged(string value) => InvalidatePreview();
    partial void OnUseX64RuleChanged(bool value) => InvalidatePreview();
    partial void OnUseArm64RuleChanged(bool value) => InvalidatePreview();
    partial void OnUseX86RuleChanged(bool value) => InvalidatePreview();
    partial void OnUseAnyRuleChanged(bool value) => InvalidatePreview();
    partial void OnX64IncludeChanged(string value) { if (!_loading && !string.IsNullOrWhiteSpace(value)) UseX64Rule = true; InvalidatePreview(); }
    partial void OnX64ExcludeChanged(string value) { if (!_loading && !string.IsNullOrWhiteSpace(value)) UseX64Rule = true; InvalidatePreview(); }
    partial void OnArm64IncludeChanged(string value) { if (!_loading && !string.IsNullOrWhiteSpace(value)) UseArm64Rule = true; InvalidatePreview(); }
    partial void OnArm64ExcludeChanged(string value) { if (!_loading && !string.IsNullOrWhiteSpace(value)) UseArm64Rule = true; InvalidatePreview(); }
    partial void OnX86IncludeChanged(string value) { if (!_loading && !string.IsNullOrWhiteSpace(value)) UseX86Rule = true; InvalidatePreview(); }
    partial void OnX86ExcludeChanged(string value) { if (!_loading && !string.IsNullOrWhiteSpace(value)) UseX86Rule = true; InvalidatePreview(); }
    partial void OnAnyIncludeChanged(string value) { if (!_loading && !string.IsNullOrWhiteSpace(value)) UseAnyRule = true; InvalidatePreview(); }
    partial void OnAnyExcludeChanged(string value) { if (!_loading && !string.IsNullOrWhiteSpace(value)) UseAnyRule = true; InvalidatePreview(); }

    private void InvalidatePreview()
    {
        if (_loading) return;
        _previewedPolicy = null; _previewedId = null;
        Preview = string.Empty; Status = string.Empty;
        OnPropertyChanged(nameof(CanSave));
    }

    private PackagePolicy BuildPolicy()
    {
        var rules = new List<PackageAssetRule>();
        if (UseX64Rule) AddRule(Arch.Amd64, X64Include, X64Exclude);
        if (UseArm64Rule) AddRule(Arch.Arm64, Arm64Include, Arm64Exclude);
        if (UseX86Rule) AddRule(Arch.X86, X86Include, X86Exclude);
        if (UseAnyRule) AddRule(Arch.Unknown, AnyInclude, AnyExclude);
        var policy = new PackagePolicy
        {
            ReleaseChannel = Channel?.Value ?? PackageReleaseChannel.Stable,
            PinnedTag = Channel?.Value == PackageReleaseChannel.Pinned ? PinnedTag.Trim() : null,
            InstallFormat = Format?.Value ?? PackageInstallFormat.Auto,
            ExecutableRelativePath = string.IsNullOrWhiteSpace(ExecutablePath) ? null : ExecutablePath.Trim(),
            PreservePaths = Lines(PreservePaths), AssetRules = rules,
        };
        PackagePolicyValidator.Validate(policy);
        return policy;
        void AddRule(Arch architecture, string include, string exclude)
        {
            var includes = Lines(include); var excludes = Lines(exclude);
            rules.Add(new PackageAssetRule { Architecture = architecture, Include = includes, Exclude = excludes });
        }
    }

    [RelayCommand]
    public void PreviewPolicy()
    {
        if (!CanEdit || SelectedEntry is not { } selected) return;
        InvalidatePreview();
        try
        {
            var policy = BuildPolicy();
            var detail = selected.Detail with { PackagePolicy = policy };
            var release = DefaultAssetSelectionPolicy.SelectRelease(detail);
            var rows = new List<string> { _text["LibraryEditorPreviewLocal"], release?.TagName ?? _text["LibraryDiagnosticNoRelease"] };
            foreach (var arch in new[] { Arch.Amd64, Arch.Arm64, Arch.X86 })
                rows.Add($"{arch}: {DefaultAssetSelectionPolicy.Select(detail, arch)?.Name ?? _text["LibraryDiagnosticNoMatchingAsset"]}");
            Preview = string.Join(Environment.NewLine, rows);
            _previewedPolicy = policy; _previewedId = detail.App.Id;
            OnPropertyChanged(nameof(CanSave));
        }
        catch (RepositoryCatalogException) { Status = _text["LibraryEditorInvalid"]; }
    }

    /// <summary>Saving atomically refreshes the configured release via the catalog service.</summary>
    [RelayCommand]
    public async Task SavePolicyAsync(CancellationToken ct = default)
    {
        if (!CanSave || SelectedEntry is not { } selected || _previewedPolicy is not { } policy) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        IsBusy = true;
        try
        {
            var result = await _catalog.UpdatePackagePolicyAsync(selected.Detail.App.Id, policy, cancellation.Token);
            if (_disposed) return;
            var index = Entries.IndexOf(selected);
            if (index >= 0) Entries[index] = new LibraryEntryChoice(result);
            SelectedEntry = index >= 0 ? Entries[index] : null;
            Status = _text["LibraryEditorSaved"];
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { if (!_disposed) Status = _text["LibraryEditorCancelled"]; }
        catch (Exception) { if (!_disposed) Status = _text["LibraryEditorSaveFailed"]; }
        finally { if (!_disposed) IsBusy = false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _text.LanguageChanged -= OnLanguageChanged;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private static string[] Lines(string value) => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static string Patterns(PackagePolicy policy, Arch arch, bool include) => string.Join(Environment.NewLine,
        policy.AssetRules.FirstOrDefault(r => r.Architecture == arch) is { } rule ? include ? rule.Include : rule.Exclude : []);
}

public sealed class LibraryEntryChoice(AppDetail detail)
{
    public AppDetail Detail { get; } = detail;
    public string Name => $"{Detail.App.Owner}/{Detail.App.Repo}";
}
