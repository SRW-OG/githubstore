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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunLibraryTools), nameof(LibraryToolStatus))]
    private bool _isLibraryToolBusy;

    [ObservableProperty] private string _libraryToolError = string.Empty;
    [ObservableProperty] private string _libraryExportPath = string.Empty;

    /// <summary>自检结果，按仓库键汇总；为空表示没有发现问题（不等于下载后一定能装成功）。</summary>
    public ObservableCollection<LibraryDiagnosticViewModel> LibraryDiagnostics { get; } = [];

    public bool CanRunLibraryTools => !_disposed && !IsLibraryToolBusy && _catalog is not null;
    public bool HasLibraryDiagnostics => LibraryDiagnostics.Count > 0;
    public string LibraryToolStatus => IsLibraryToolBusy ? Text["LibraryToolsBusy"]
        : LibraryDiagnostics.Count > 0 ? Text.Format("LibraryToolsFound", LibraryDiagnostics.Count)
        : Text["LibraryToolsIdle"];

    /// <summary>
    /// 读取整个库并做离线自检。逐条按 ID 取详情：目录列表接口只返回展示用摘要，
    /// 而自检与导出都需要真实的发行包与校验信息，不能拿摘要代替。
    /// </summary>
    [RelayCommand]
    public async Task InspectLibraryAsync(CancellationToken ct = default)
    {
        if (_disposed || IsLibraryToolBusy || _catalog is not { } catalog) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _catalogLibraryLifetime.Token);
        _libraryToolsCancellation = cancellation;
        IsLibraryToolBusy = true;
        LibraryToolError = string.Empty;
        try
        {
            var entries = await LoadLibraryEntriesAsync(catalog, cancellation.Token);
            if (_disposed || cancellation.IsCancellationRequested) return;
            LibraryDiagnostics.Clear();
            foreach (var diagnostic in CatalogLibraryDiagnostics.Inspect(entries, CurrentArchitecture))
                LibraryDiagnostics.Add(new LibraryDiagnosticViewModel(diagnostic, Text));
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
    /// 导出可再次同步的库文件。写盘位置由调用方（文件保存选择器）提供；
    /// 导出前先自校验，导出的字节必须能被本程序自己的加载器读回。
    /// </summary>
    public async Task<bool> ExportLibraryAsync(string destinationPath, CancellationToken ct = default)
    {
        if (_disposed || _catalog is not { } catalog || string.IsNullOrWhiteSpace(destinationPath)) return false;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _catalogLibraryLifetime.Token);
        _libraryToolsCancellation = cancellation;
        IsLibraryToolBusy = true;
        LibraryToolError = string.Empty;
        try
        {
            var entries = await LoadLibraryEntriesAsync(catalog, cancellation.Token);
            var categories = await catalog.ListCategoriesAsync(cancellation.Token);
            var payload = await CatalogLibraryExporter.ExportAsync(entries, categories, DateTimeOffset.UtcNow, cancellation.Token);
            if (_disposed || cancellation.IsCancellationRequested) return false;
            // 原子写入：先写临时文件再替换，避免中断留下半份库文件。
            var temporary = destinationPath + ".tmp";
            await File.WriteAllBytesAsync(temporary, payload, cancellation.Token);
            File.Move(temporary, destinationPath, overwrite: true);
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

    /// <summary>
    /// 分页读取本地目录并逐条取回详情。上限与 AI 目录加载一致，防止超大库拖垮界面。
    /// </summary>
    private static async Task<IReadOnlyList<AppDetail>> LoadLibraryEntriesAsync(ICatalogService catalog, CancellationToken ct)
    {
        var details = new List<AppDetail>();
        var offset = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = await catalog.BrowseAsync(new CatalogQuery { Offset = offset, Limit = 100 }, ct).ConfigureAwait(false);
            foreach (var app in page.Items)
            {
                ct.ThrowIfCancellationRequested();
                try { details.Add(await catalog.GetAppAsync(app.Id, ct).ConfigureAwait(false)); }
                catch (RepositoryCatalogException) { /* 条目在这一刻已不可读：跳过它，不编造内容。 */ }
            }
            if (!page.HasMore) break;
            if (page.Items.Count == 0 || details.Count > 2000 || offset > 10000) break;
            offset += page.Items.Count;
        }
        return details.AsReadOnly();
    }

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
