using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitHubStore.App.Services.Localization;
using GitHubStore.Core.Abstractions;
using GitHubStore.Core.Catalog;

namespace GitHubStore.App.ViewModels;

/// <summary>软件库维护：检查配置问题并把当前库导出成可再次同步的文件。</summary>
public sealed partial class SettingsViewModel
{
    private CancellationTokenSource? _libraryToolsCancellation;
    private bool _libraryInspectionCompleted;
    private LibraryPolicyEditorViewModel? _libraryEditor;
    public LibraryPolicyEditorViewModel? LibraryEditor => _catalog is IPackagePolicyCatalogService policies
        ? _libraryEditor ??= new LibraryPolicyEditorViewModel(policies, Text) : null;

    [RelayCommand]
    public async Task LoadLibraryEditorAsync(CancellationToken ct = default)
    {
        if (!CanRunLibraryTools) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _catalogLibraryLifetime.Token);
        _libraryToolsCancellation = cancellation;
        IsLibraryToolBusy = true;
        LibraryToolError = string.Empty;
        try
        {
            var snapshot = await ReadLocalLibraryAsync(cancellation.Token);
            if (!_disposed) LibraryEditor?.SetEntries(snapshot.Entries);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception) { if (!_disposed) LibraryToolError = Text["LibraryToolsInspectFailed"]; }
        finally
        {
            if (ReferenceEquals(_libraryToolsCancellation, cancellation)) _libraryToolsCancellation = null;
            if (!_disposed) IsLibraryToolBusy = false;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunLibraryTools), nameof(LibraryToolStatus))]
    private bool _isLibraryToolBusy;

    [ObservableProperty] private string _libraryToolError = string.Empty;
    [ObservableProperty] private string _libraryExportPath = string.Empty;

    /// <summary>自检结果，按仓库键汇总；为空表示没有发现问题（不等于下载后一定能装成功）。</summary>
    public ObservableCollection<LibraryDiagnosticViewModel> LibraryDiagnostics { get; } = [];

    public bool CanRunLibraryTools => !_disposed && !IsLibraryToolBusy && _catalog is ILocalCatalogSnapshotService;
    public bool HasLibraryDiagnostics => LibraryDiagnostics.Count > 0;
    public string LibraryToolStatus => IsLibraryToolBusy ? Text["LibraryToolsBusy"]
        : LibraryDiagnostics.Count > 0 ? Text.Format("LibraryToolsFound", LibraryDiagnostics.Count)
        : _libraryInspectionCompleted ? Text["LibraryToolsNoProblems"] : Text["LibraryToolsIdle"];

    /// <summary>
    /// 读取一次完整本地快照并做离线自检，不分页截断，不用联网详情接口补全。
    /// </summary>
    [RelayCommand]
    public async Task InspectLibraryAsync(CancellationToken ct = default)
    {
        if (!CanRunLibraryTools) return;
        _libraryInspectionCompleted = false;
        LibraryDiagnostics.Clear();
        OnPropertyChanged(nameof(HasLibraryDiagnostics));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _catalogLibraryLifetime.Token);
        _libraryToolsCancellation = cancellation;
        IsLibraryToolBusy = true;
        LibraryToolError = string.Empty;
        try
        {
            var snapshot = await ReadLocalLibraryAsync(cancellation.Token);
            if (_disposed || cancellation.IsCancellationRequested) return;
            var diagnostics = CatalogLibraryDiagnostics.Inspect(snapshot.Entries, CurrentArchitecture)
                .Select(diagnostic => new LibraryDiagnosticViewModel(diagnostic, Text)).ToArray();
            foreach (var diagnostic in diagnostics) LibraryDiagnostics.Add(diagnostic);
            _libraryInspectionCompleted = true;
            OnPropertyChanged(nameof(HasLibraryDiagnostics));
            OnPropertyChanged(nameof(LibraryToolStatus));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (RepositoryCatalogException) { if (!_disposed) LibraryToolError = Text["LibraryToolsInspectFailed"]; }
        catch (Exception) { if (!_disposed) LibraryToolError = Text["LibraryToolsInspectFailed"]; }
        finally
        {
            if (ReferenceEquals(_libraryToolsCancellation, cancellation)) _libraryToolsCancellation = null;
            if (!_disposed) IsLibraryToolBusy = false;
        }
    }

    /// <summary>
    /// 导出完整本地备份（不是公开同步源）。写盘位置由调用方（文件保存选择器）提供；
    /// 导出前先自校验，导出的字节必须能被本程序自己的加载器读回。
    /// </summary>
    public async Task<bool> ExportLibraryAsync(string destinationPath, CancellationToken ct = default)
    {
        if (!CanRunLibraryTools || string.IsNullOrWhiteSpace(destinationPath)) return false;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _catalogLibraryLifetime.Token);
        _libraryToolsCancellation = cancellation;
        IsLibraryToolBusy = true;
        LibraryToolError = string.Empty;
        try
        {
            var snapshot = await ReadLocalLibraryAsync(cancellation.Token);
            var payload = await CatalogLibraryExporter.ExportLocalAsync(snapshot.Entries, snapshot.Categories, DateTimeOffset.UtcNow, cancellation.Token);
            if (_disposed || cancellation.IsCancellationRequested) return false;
            var destination = Path.GetFullPath(destinationPath);
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(payload, cancellation.Token);
                    await stream.FlushAsync(cancellation.Token);
                }
                cancellation.Token.ThrowIfCancellationRequested();
                File.Move(temporary, destination, overwrite: true);
            }
            finally
            {
                // Only the unique sibling created by this operation is eligible for cleanup.
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            if (!_disposed) LibraryExportPath = destinationPath;
            return true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return false; }
        catch (Exception) { if (!_disposed) LibraryToolError = Text["LibraryToolsExportFailed"]; return false; }
        finally
        {
            if (ReferenceEquals(_libraryToolsCancellation, cancellation)) _libraryToolsCancellation = null;
            if (!_disposed) IsLibraryToolBusy = false;
        }
    }

    [RelayCommand]
    private void CancelLibraryTools() => _libraryToolsCancellation?.Cancel();

    private Task<LocalCatalogSnapshot> ReadLocalLibraryAsync(CancellationToken ct) =>
        _catalog is ILocalCatalogSnapshotService local
            ? local.ReadLocalSnapshotAsync(ct)
            : throw new InvalidOperationException("The catalog does not support a complete offline snapshot.");

    /// <summary>
    /// 一条自检结果的展示包装：把稳定的诊断种类翻译成当前语言，
    /// 界面不直接显示枚举名，也不擅自给出安全结论。
    /// </summary>
    public sealed class LibraryDiagnosticViewModel
    {
        public LibraryDiagnosticViewModel(LibraryDiagnostic diagnostic, LocalizationService text)
        {
            RepositoryKey = diagnostic.RepositoryKey;
            Detail = diagnostic.Detail ?? string.Empty;
            Kind = text[diagnostic.Kind switch
            {
                LibraryDiagnosticKind.NoMatchingAsset => "LibraryDiagnosticNoMatchingAsset",
                LibraryDiagnosticKind.NoRelease => "LibraryDiagnosticNoRelease",
                LibraryDiagnosticKind.PinnedTagMissing => "LibraryDiagnosticPinnedTagMissing",
                LibraryDiagnosticKind.UnsafeDownloadUrl => "LibraryDiagnosticUnsafeDownloadUrl",
                LibraryDiagnosticKind.MissingDigest => "LibraryDiagnosticMissingDigest",
                // 未知种类用通用提示，不把未识别的问题说成某个已知问题。
                _ => "LibraryToolsInspectFailed",
            }];
        }

        public string RepositoryKey { get; }
        public string Kind { get; }
        public string Detail { get; }
    }

    private static Arch CurrentArchitecture => System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.Arm64 => Arch.Arm64,
        System.Runtime.InteropServices.Architecture.X86 => Arch.X86,
        System.Runtime.InteropServices.Architecture.X64 => Arch.Amd64,
        _ => Arch.Unknown,
    };
}
