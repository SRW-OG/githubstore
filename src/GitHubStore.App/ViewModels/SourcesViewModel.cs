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
        Packages.Clear();
        foreach (var item in items) Packages.Add(item);
        OnPropertyChanged(nameof(IsEmpty));
        ResultNotice = Text[items.Count > 0 && items.All(p => p.IsStatic) ? "WingetStaticNotice" : "WingetLiveNotice"];
    }

    private void UpdateStatus() => WingetStatus = !_initialized ? Text["WingetNotProbed"]
        : _wingetAvailable ? Text["WingetAvailable"] : ErrorText(_winget.LastProbeErrorCode ?? WingetErrorCodes.CommandFailed);

    private void OnLanguageChanged(object? sender, EventArgs args)
    {
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
