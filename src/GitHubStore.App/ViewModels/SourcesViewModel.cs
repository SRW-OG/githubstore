using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using GitHubStore.App.Services.Localization;
using GitHubStore.Core.Abstractions;

namespace GitHubStore.App.ViewModels;

/// <summary>静态精选与实时查询分开标记，探测/查询都有可等待的生命周期。</summary>
public sealed partial class SourcesViewModel : ObservableObject, IDisposable
{
    private readonly IWingetService _winget;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _initialization;
    private bool _disposed;
    private bool _wingetAvailable;
    private bool _initialized;
    private CancellationTokenSource? _detailsCancellation;
    private readonly Dictionary<string, WingetPackage> _detailsCache = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _installCancellation;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyPropertyChangedFor(nameof(CanCancelInstall))]
    private bool _isInstalling;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInstallStatus))]
    private string _installStatus = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInstallOutput))]
    private string _installOutput = string.Empty;
    public bool HasInstallStatus => !string.IsNullOrWhiteSpace(InstallStatus);
    public bool HasInstallOutput => !string.IsNullOrWhiteSpace(InstallOutput);
    public bool CanInstall => !_disposed && !IsInstalling && SelectedPackage is not null;
    public bool CanCancelInstall => !_disposed && IsInstalling;

    public async Task InstallSelectedAsync(CancellationToken ct = default)
    {
        if (!CanInstall || SelectedPackage is not { } package) return;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        _installCancellation = operation;
        IsInstalling = true;
        InstallOutput = string.Empty;
        var name = string.IsNullOrWhiteSpace(package.Name) ? package.Id : package.Name;
        InstallStatus = Text.Format("WingetInstalling", name);
        try
        {
            var result = await _winget.InstallAsync(package.Id, operation.Token);
            if (_disposed) return;
            operation.Token.ThrowIfCancellationRequested();
            InstallOutput = result.Output ?? string.Empty;
            if (result.Succeeded)
                InstallStatus = Text.Format(result.RebootRequired ? "WingetInstalledRestart" : "WingetInstalled", name);
            else
            {
                InstallStatus = Text.Format("WingetInstallFailed", name) + " "
                    + Text[result.ErrorCode switch
                    {
                        WingetErrorCodes.Busy => "WingetInstallBusy",
                        WingetErrorCodes.TimedOut => "WingetInstallTimedOut",
                        WingetErrorCodes.NotInstalled => "WingetInstallUnavailable",
                        _ => "WingetInstallCheckOutput"
                    }];
                if (result.ExitCode is { } exitCode) InstallStatus += $" (0x{exitCode:X8})";
            }
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            if (!_disposed) InstallStatus = Text.Format("WingetInstallCancelled", name);
        }
        catch (Exception) { if (!_disposed) InstallStatus = Text.Format("WingetInstallFailed", name); }
        finally
        {
            _installCancellation = null;
            if (!_disposed) IsInstalling = false;
        }
    }

    public async Task CancelInstallAsync()
    {
        var operation = _installCancellation;
        if (operation is not null) await operation.CancelAsync();
    }
    [ObservableProperty] private WingetPackage? _selectedPackage;
    [ObservableProperty] private WingetPackage? _packageDetails;
    [ObservableProperty] private bool _isDetailsBusy;
    [ObservableProperty] private string _detailsError = string.Empty;
    public bool HasSelectedPackage => SelectedPackage is not null;
    public bool HasPackageDetails => PackageDetails is not null;
    public bool HasDetailsError => !string.IsNullOrEmpty(DetailsError);
    public string DetailPublisher => DetailValue(PackageDetails?.Publisher);
    public string DetailDescription => DetailValue(PackageDetails?.Description);
    public string DetailLicense => DetailValue(PackageDetails?.License);
    public string DetailVersion => DetailValue(PackageDetails?.Version);
    public string DetailHomepage => DetailValue(PackageDetails?.Homepage);
    public string DetailPublisherUrl => DetailValue(PackageDetails?.PublisherUrl);
    public string DetailLicenseUrl => DetailValue(PackageDetails?.LicenseUrl);
    public string DetailReleaseNotes => DetailValue(PackageDetails?.ReleaseNotes);
    public string DetailReleaseNotesUrl => DetailValue(PackageDetails?.ReleaseNotesUrl);
    private string DetailValue(string? value) => string.IsNullOrWhiteSpace(value) ? Text["WingetNotProvided"] : value;
    partial void OnSelectedPackageChanged(WingetPackage? value)
    {
        OnPropertyChanged(nameof(HasSelectedPackage));
        OnPropertyChanged(nameof(CanInstall));
    }
    partial void OnDetailsErrorChanged(string value) => OnPropertyChanged(nameof(HasDetailsError));
    partial void OnPackageDetailsChanged(WingetPackage? value) => NotifyDetails();
    private void NotifyDetails()
    {
        foreach (var name in new[] { nameof(HasPackageDetails), nameof(DetailPublisher), nameof(DetailDescription),
            nameof(DetailLicense), nameof(DetailVersion), nameof(DetailHomepage), nameof(DetailPublisherUrl),
            nameof(DetailLicenseUrl), nameof(DetailReleaseNotes), nameof(DetailReleaseNotesUrl) }) OnPropertyChanged(name);
    }

    public async Task LoadDetailsAsync(WingetPackage? package, CancellationToken ct = default)
    {
        if (_disposed) return;
        var previous = _detailsCancellation;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        _detailsCancellation = operation;
        SelectedPackage = package;
        PackageDetails = null;
        DetailsError = string.Empty;
        IsDetailsBusy = package is not null;
        bool IsCurrent() => !_disposed && ReferenceEquals(_detailsCancellation, operation) && !operation.IsCancellationRequested;
        try
        {
            if (previous is not null) await previous.CancelAsync();
            if (package is null || !IsCurrent()) return;
            if (_detailsCache.TryGetValue(package.Id, out var cached)) { PackageDetails = cached; return; }
            var result = await _winget.GetDetailsAsync(package.Id, operation.Token);
            if (!IsCurrent()) return;
            if (result.Succeeded && result.Package is { } details && string.Equals(details.Id, package.Id, StringComparison.OrdinalIgnoreCase))
            {
                _detailsCache[package.Id] = details;
                PackageDetails = details;
            }
            else DetailsError = Text["WingetDetailsFailed"] + " " + ErrorText(result.ErrorCode ?? WingetErrorCodes.InvalidOutput);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
        catch (Exception) { if (IsCurrent()) DetailsError = Text["WingetDetailsFailed"]; }
        finally
        {
            if (ReferenceEquals(_detailsCancellation, operation)) { _detailsCancellation = null; if (!_disposed) IsDetailsBusy = false; }
        }
    }

    public SourcesViewModel(IWingetService winget, LocalizationService text)
    {
        _winget = winget;
        Text = text;
        Text.LanguageChanged += OnLanguageChanged;
        Replace(winget.ListFeatured());
        WingetStatus = Text["WingetNotProbed"];
    }

    public LocalizationService Text { get; }
    public ObservableCollection<WingetPackage> Packages { get; } = [];
    [ObservableProperty] private string _wingetQuery = string.Empty;
    [ObservableProperty] private string _wingetStatus = string.Empty;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private string _resultNotice = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _isBusy;
    public bool IsEmpty => !IsBusy && Packages.Count == 0;

    public Task InitializeAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized) return Task.CompletedTask;
        return _initialization is { IsCompleted: false } ? _initialization : _initialization = InitializeCoreAsync(ct);
    }

    private async Task InitializeCoreAsync(CancellationToken ct)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        try
        {
            _wingetAvailable = await _winget.ProbeAsync(cancellation.Token);
            if (_disposed) return;
            _initialized = true;
            UpdateStatus();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!_disposed) _initialization = null;
        }
    }

    public async Task SearchAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsBusy) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        var token = cancellation.Token;
        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            await InitializeAsync(token);
            token.ThrowIfCancellationRequested();
            if (!_wingetAvailable)
            {
                ErrorMessage = ErrorText(_winget.LastProbeErrorCode ?? WingetErrorCodes.CommandFailed);
                return;
            }
            if (string.IsNullOrWhiteSpace(WingetQuery))
            {
                Replace(_winget.ListFeatured());
                return;
            }
            var result = await _winget.SearchAsync(WingetQuery.Trim(), 30, token);
            if (_disposed) return;
            if (!result.Succeeded)
            {
                ErrorMessage = ErrorText(result.ErrorCode ?? WingetErrorCodes.CommandFailed);
                return; // 保留此前结果，失败不伪装成无匹配项。
            }
            Replace(result.Items);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!_disposed) ErrorMessage = Text["WingetCommandFailed"];
        }
        finally { if (!_disposed) IsBusy = false; }
    }

    private string ErrorText(string code) => Text[code switch
    {
        WingetErrorCodes.NotInstalled => "WingetUnavailable",
        WingetErrorCodes.TimedOut => "WingetTimedOut",
        WingetErrorCodes.InvalidOutput => "WingetInvalidOutput",
        _ => "WingetCommandFailed"
    }];

    private void Replace(IReadOnlyList<WingetPackage> items)
    {
        _ = LoadDetailsAsync(null);
        Packages.Clear();
        foreach (var item in items) Packages.Add(item);
        OnPropertyChanged(nameof(IsEmpty));
        ResultNotice = Text[items.Count > 0 && items.All(p => p.IsStatic) ? "WingetStaticNotice" : "WingetLiveNotice"];
    }

    private void UpdateStatus() => WingetStatus = !_initialized ? Text["WingetNotProbed"]
        : _wingetAvailable ? Text["WingetAvailable"] : ErrorText(_winget.LastProbeErrorCode ?? WingetErrorCodes.CommandFailed);

    private void OnLanguageChanged(object? sender, EventArgs args)
    {
        NotifyDetails();
        UpdateStatus();
        ResultNotice = Text[Packages.Count > 0 && Packages.All(p => p.IsStatic) ? "WingetStaticNotice" : "WingetLiveNotice"];
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Text.LanguageChanged -= OnLanguageChanged;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
