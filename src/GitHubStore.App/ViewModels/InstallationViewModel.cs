using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitHubStore.App.Services.Localization;
using GitHubStore.Core.Abstractions;
using CatalogApp = GitHubStore.Core.Abstractions.App;

namespace GitHubStore.App.ViewModels;

/// <summary>Installation state independent of catalog selection. Call from the UI context.</summary>
public sealed partial class InstallationViewModel : ObservableObject, IDisposable
{
    private readonly IAppInstallService _service;
    private CancellationTokenSource? _current;
    private int _installGate;
    private bool _disposed;
    private string? _statusKey;
    private string? _errorKey;
    private bool _filesMayRemain;
    private bool _requiresRestart;

    public InstallationViewModel(IAppInstallService service, LocalizationService text,
        GitHubStore.App.Services.Ai.IAiAssistantService? assistant = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(text);
        _service = service;
        _assistant = assistant;
        Text = text;
        CancelCommand = new AsyncRelayCommand(CancelAsync, () => CanCancel);
        Text.LanguageChanged += OnLanguageChanged;
    }

    public LocalizationService Text { get; }
    public IAsyncRelayCommand CancelCommand { get; }
    [ObservableProperty] private bool _isInstalling;
    [ObservableProperty] private double _percent;
    [ObservableProperty] private bool _isIndeterminate;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private string _targetDirectory = string.Empty;
    [ObservableProperty] private string _executablePath = string.Empty;
    public bool CanCancel => !_disposed && IsInstalling && _current is { IsCancellationRequested: false };

    public string GetTargetDirectory(CatalogApp app) => _service.GetTargetDirectory(app);

    /// <summary>Rejects overlapping calls without replacing the active installation's state.</summary>
    public async Task InstallAsync(AppInstallRequest request,
        Func<IReadOnlyList<string>, CancellationToken, Task<string?>>? chooseExecutable = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        if (Interlocked.CompareExchange(ref _installGate, 1, 0) != 0)
            throw new InvalidOperationException(Text["InstallBusy"]);

        using var cancellation = new CancellationTokenSource();
        _current = cancellation;
        ResetDiagnosis();
        try
        {
            _statusKey = "InstallPreparing";
            _errorKey = null;
            _filesMayRemain = false;
            _requiresRestart = false;
            Percent = 0;
            IsIndeterminate = true;
            TargetDirectory = string.Empty;
            ExecutablePath = string.Empty;
            IsInstalling = true;
            RefreshMessages();
            NotifyCancellationChanged();
            TargetDirectory = GetTargetDirectory(request.App);

            // Progress captures the caller's SynchronizationContext, including the WinUI UI context.
            var progress = new Progress<AppInstallProgress>(update =>
            {
                if (!IsCurrent(cancellation) || cancellation.IsCancellationRequested) return;
                _statusKey = PhaseKey(update.Phase);
                IsIndeterminate = update.Percent is not { } value || !double.IsFinite(value);
                if (!IsIndeterminate) Percent = Math.Clamp(update.Percent!.Value, 0, 100);
                RefreshMessages();
            });
            var result = await _service.InstallAsync(request, progress, chooseExecutable, cancellation.Token);
            if (!IsCurrent(cancellation)) return;
            _lastDiagnostic = result.Succeeded ? null : result.Diagnostic ?? new InstallDiagnostic
            {
                OperationId = Guid.NewGuid().ToString("N"), AppName = request.App.DisplayName,
                Version = request.Release.TagName, AssetName = request.Asset.Name,
                Phase = AppInstallPhase.Preparing, ErrorCode = ErrorKey(result.ErrorCode),
                FilesMayRemain = result.FilesMayRemain, FailedAt = DateTimeOffset.UtcNow,
            };

            // A cancelled external installer may still have written files. Preserve the service's warning.
            _filesMayRemain = result.FilesMayRemain;
            _requiresRestart = result.RequiresRestart;
            TargetDirectory = result.TargetDirectory ?? TargetDirectory;
            ExecutablePath = result.ExecutablePath ?? string.Empty;
            IsIndeterminate = false;
            if (result.Succeeded)
            {
                Percent = 100;
                _statusKey = "InstallCompleted";
                _errorKey = null;
            }
            else
            {
                var key = ErrorKey(result.ErrorCode);
                var cancelled = key == "InstallCancelled" || cancellation.IsCancellationRequested;
                _statusKey = cancelled ? "InstallCancelled" : "InstallFailed";
                _errorKey = cancelled ? null : key;
            }
            RefreshMessages();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!IsCurrent(cancellation)) return;
            IsIndeterminate = false;
            _statusKey = "InstallCancelled";
            _errorKey = null;
            RefreshMessages();
        }
        catch (Exception ex)
        {
            if (!IsCurrent(cancellation)) return;
            IsIndeterminate = false;
            _statusKey = "InstallFailed";
            _errorKey = ex switch
            {
                UnauthorizedAccessException => "InstallAccessDenied",
                InvalidDataException => "InstallInvalidArchive",
                _ => ErrorKey(ex.Message),
            };
            RefreshMessages();
        }
        finally
        {
            if (ReferenceEquals(_current, cancellation)) _current = null;
            if (!_disposed)
            {
                IsInstalling = false;
                NotifyCancellationChanged();
                NotifyDiagnosisChanged();
            }
            Interlocked.Exchange(ref _installGate, 0);
        }
    }

    private bool IsCurrent(CancellationTokenSource cancellation)
        => !_disposed && ReferenceEquals(_current, cancellation) && IsInstalling;

    private async Task CancelAsync()
    {
        var cancellation = _current;
        if (!CanCancel || cancellation is null) return;
        try
        {
            var pending = cancellation.CancelAsync();
            NotifyCancellationChanged();
            await pending;
        }
        catch (ObjectDisposedException) { /* The operation completed while cancellation was requested. */ }
        catch (Exception)
        {
            // A service cancellation callback must not surface an exception through the UI command.
            if (!_disposed && ReferenceEquals(_current, cancellation))
            {
                _errorKey = "InstallFailed";
                RefreshMessages();
            }
        }
    }

    private void NotifyCancellationChanged()
    {
        OnPropertyChanged(nameof(CanCancel));
        CancelCommand.NotifyCanExecuteChanged();
    }

    private void RefreshMessages()
    {
        StatusMessage = _statusKey is null ? string.Empty : Text[_statusKey];
        if (_requiresRestart) StatusMessage = Join(StatusMessage, Text["InstallRestartRequired"]);
        if (_filesMayRemain) StatusMessage = Join(StatusMessage, Text["InstallFilesMayRemain"]);
        ErrorMessage = _errorKey is null ? string.Empty : Text[_errorKey];
    }

    private static string Join(string message, string warning)
        => message.Length == 0 ? warning : message + Environment.NewLine + warning;

    private static string PhaseKey(AppInstallPhase phase) => phase switch
    {
        AppInstallPhase.Downloading => "InstallDownloading",
        AppInstallPhase.UsingCache => "InstallUsingCache",
        AppInstallPhase.Extracting => "InstallExtracting",
        AppInstallPhase.RunningInstaller => "InstallRunningInstaller",
        AppInstallPhase.Verifying => "InstallVerifying",
        AppInstallPhase.Registering => "InstallRegistering",
        AppInstallPhase.Completed => "InstallCompleted",
        _ => "InstallPreparing",
    };

    /// <summary>Maps only recognized codes to resource keys; never displays service/exception text.</summary>
    public static string ErrorKey(string? errorCode)
    {
        var code = new string((errorCode ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        foreach (var prefix in new[] { "appinstallation", "appinstall", "installation", "install" })
        {
            if (!code.StartsWith(prefix, StringComparison.Ordinal)) continue;
            code = code[prefix.Length..];
            break;
        }
        return code switch
        {
            "incompatibleasset" or "unsupportedasset" => "InstallIncompatible",
            "unsafepath" or "invalidrequest" => "InstallUnsafePath",
            "registrationfailed" => "InstallRegistrationFailed",
            "installerfailed" => "InstallInstallerFailed",
            "installercancelled" or "installercanceled" => "InstallCancelled",
            "alreadyinstalled" => "InstallTargetExists",
            "targetnothonored" or "executableinvalid" or "executablechoicerequired" or "invalidexecutablechoice" => "InstallEntryNotFound",
            "archivelimit" => "InstallInvalidArchive",
            "checksummismatch" or "lengthmismatch" or "invaliddigest" => "InstallDownloadFailed",
            "cancelled" or "canceled" or "operationcancelled" or "operationcanceled" => "InstallCancelled",
            "unsupported7z" or "7zunsupported" or "unsupported7zip" or "unsupportedarchive" => "InstallUnsupported7z",
            "targetexists" or "targetdirectoryexists" or "targetalreadyexists" or "directoryexists" => "InstallTargetExists",
            "entrynotfound" or "entrypointnotfound" or "executablenotfound" or "noexecutable" or "executablemissing" => "InstallEntryNotFound",
            "invalidarchive" or "archiveinvalid" or "unsafearchive" or "archivepathtraversal" => "InstallInvalidArchive",
            "downloadfailed" or "downloadfailure" or "networkerror" or "downloaderror" => "InstallDownloadFailed",
            "accessdenied" or "unauthorized" or "unauthorizedaccess" or "permissiondenied" => "InstallAccessDenied",
            "busy" or "alreadyinstalling" or "installationinprogress" or "inprogress" => "InstallBusy",
            _ => "InstallFailed",
        };
    }

    private void OnLanguageChanged(object? sender, EventArgs args)
    {
        if (!_disposed)
        {
            RefreshMessages();
            OnPropertyChanged(nameof(DiagnosisLabel));
            OnPropertyChanged(nameof(CancelDiagnosisLabel));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ResetDiagnosis();
        Text.LanguageChanged -= OnLanguageChanged;
        // Async cancellation avoids executing arbitrary installer callbacks on the disposing UI thread.
        _ = CancelOnDisposeAsync(_current);
        IsInstalling = false;
        NotifyCancellationChanged();
    }

    private static async Task CancelOnDisposeAsync(CancellationTokenSource? cancellation)
    {
        if (cancellation is null) return;
        try { await cancellation.CancelAsync(); }
        catch (Exception) { /* Disposal is best effort; the operation owns and disposes the CTS. */ }
    }
}
