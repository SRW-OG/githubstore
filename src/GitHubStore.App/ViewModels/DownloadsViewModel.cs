using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitHubStore.App.Services.Download;
using GitHubStore.App.Services.Localization;
using GitHubStore.App.Services.Preferences;
using GitHubStore.Core.Abstractions;

namespace GitHubStore.App.ViewModels;

/// <summary>Downloads into the same persistent, verified package cache used by installation.</summary>
public sealed partial class DownloadsViewModel : ObservableObject, IDisposable
{
    private readonly IDownloadService _service;
    private readonly PreferencesService _preferences;
    private readonly PackageCacheService _packageCache;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _cancellation;
    private bool _disposed;

    public DownloadsViewModel(IDownloadService service, PreferencesService preferences, LocalizationService text,
        PackageCacheService? packageCache = null)
    {
        _service = service;
        _preferences = preferences;
        Text = text;
        _packageCache = packageCache ?? new PackageCacheService(
            () => StorageLocations.ResolveCacheDirectory(preferences.Current, preferences.DataDirectory));
    }

    public LocalizationService Text { get; }
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private bool _isIndeterminate;
    [ObservableProperty] private double _percent;
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private string _outputPath = string.Empty;

    public async Task DownloadAsync(Asset asset, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsDownloading) return;
        var snapshot = _preferences.Current;
        if (!Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo)
            || !PackageCacheService.IsSafeFileName(asset.Name))
        {
            Status = Text["DownloadInvalidAsset"];
            return;
        }

        IsDownloading = true;
        IsIndeterminate = true;
        Percent = 0;
        OutputPath = string.Empty;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        _cancellation = cancellation;
        try
        {
            await using var lease = await _packageCache.AcquireAsync(cancellation.Token);
            var cache = lease.Cache;
            var target = cache.TryResolvePath(uri, asset.Name);
            if (target is null) { Status = Text["DownloadInvalidAsset"]; return; }
            var size = asset.Size > 0 ? asset.Size : (long?)null;
            Status = Text["DownloadCheckingCache"];
            var cached = await cache.FindVerifiedAsync(uri, asset.Name, size, asset.Digest, cancellation.Token);
            if (cached is not null)
            {
                if (_disposed) return;
                OutputPath = cached.Path;
                Percent = 100;
                Status = Text["DownloadUsingCache"] + " · " + OutputPath;
                return;
            }

            await cache.PrepareDownloadAsync(uri, asset.Name, cancellation.Token);
            var request = new DownloadRequest
            {
                Source = uri, TargetPath = target, FileName = asset.Name,
                Engine = snapshot.DownloadEngine, UseAccelerator = snapshot.UseAccelerator,
                ExpectedSize = size, ExpectedSha256 = asset.Digest,
            };
            Status = Text["DownloadInProgress"] + " · " + asset.Name;
            var progress = new Progress<DownloadProgress>(p =>
            {
                if (_disposed || !IsDownloading || cancellation.IsCancellationRequested) return;
                IsIndeterminate = p.Total <= 0;
                Percent = p.Fraction * 100;
                Status = Text["DownloadInProgress"] + " · " + asset.Name + $" · {p.Done / 1048576d:N1} MiB"
                    + (p.Total > 0 ? $" / {p.Total / 1048576d:N1} MiB" : string.Empty);
            });
            var result = _service is ConfiguredDownloadService configured
                ? await configured.DownloadWithSnapshotAsync(request, snapshot, progress, cancellation.Token)
                : await _service.DownloadAsync(request, progress, cancellation.Token);
            if (_disposed) return;
            if (result.Succeeded)
            {
                if (!string.Equals(result.OutputPath, target, StringComparison.OrdinalIgnoreCase))
                { Status = Text["DownloadIntegrityFailed"]; return; }
                try
                {
                    await cache.RecordCompletedAsync(uri, asset.Name, size, asset.Digest, cancellation.Token);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    // 文件已下载成功，但无法写入可复用的缓存记录：如实说明，不谎称完成。
                    if (!_disposed) Status = Text["DownloadCacheRecordFailed"] + " · " + target;
                    return;
                }
                if (_disposed) return;
                OutputPath = target;
                Percent = 100;
                Status = Text["DownloadComplete"] + " · " + OutputPath;
            }
            else Status = Text[ErrorKey(result.ErrorCode)];
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { if (!_disposed) Status = Text["DownloadCancelled"]; }
        catch (InvalidDataException)
        { if (!_disposed) Status = Text["DownloadIntegrityFailed"]; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { if (!_disposed) Status = Text["DownloadTargetNotWritable"]; }
        finally
        {
            if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null;
            if (!_disposed) { IsDownloading = false; IsIndeterminate = false; }
        }
    }

    private static string ErrorKey(string? code) => code switch
    {
        DownloadErrorCodes.Cancelled => "DownloadCancelled",
        DownloadErrorCodes.NoTargetDirectory or DownloadErrorCodes.TargetNotWritable => "DownloadTargetNotWritable",
        DownloadErrorCodes.Aria2NotConfigured or DownloadErrorCodes.Aria2NotFound => "DownloadAria2NotConfigured",
        DownloadErrorCodes.HttpStatusError => "DownloadHttpStatus",
        DownloadErrorCodes.ChecksumMismatch or DownloadErrorCodes.LengthMismatch or DownloadErrorCodes.InvalidResponse
            or DownloadErrorCodes.InvalidVerification => "DownloadIntegrityFailed",
        DownloadErrorCodes.PeerNotSupported => "DownloadPeerPending",
        _ => "DownloadNetworkError",
    };

    [RelayCommand]
    private Task CancelAsync() => _cancellation?.CancelAsync() ?? Task.CompletedTask;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
