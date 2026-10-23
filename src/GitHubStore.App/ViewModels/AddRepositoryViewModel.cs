using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitHubStore.App.Services.Localization;
using GitHubStore.Core.Abstractions;

namespace GitHubStore.App.ViewModels;

public sealed partial class AddRepositoryViewModel : ObservableObject, IDisposable
{
    private readonly IRepositoryCatalogService _repositories;
    private readonly CatalogViewModel _catalog;
    private readonly CancellationTokenSource _lifetime = new();
    private int _version;
    private string? _previewedInput;
    private bool _disposed;

    public AddRepositoryViewModel(IRepositoryCatalogService repositories, CatalogViewModel catalog)
    {
        _repositories = repositories;
        _catalog = catalog;
        Text = catalog.Text;
        Categories = [];
        RefreshCategories();
    }

    /// <summary>
    /// 每次打开对话框前重建分类：DI 构造发生在目录初始化之前，
    /// 且分类显示名需要跟随当前语言。
    /// </summary>
    public void RefreshCategories()
    {
        var previous = SelectedCategory?.Id;
        Categories.Clear();
        foreach (var category in _catalog.Categories)
            Categories.Add(category with { Name = Text[$"category.{category.Id.Value}"] });
        SelectedCategory = Categories.FirstOrDefault(c => previous is { } id && c.Id == id)
            ?? Categories.FirstOrDefault(c => c.Id.Value == "system")
            ?? Categories.FirstOrDefault();
    }

    public LocalizationService Text { get; }
    public ObservableCollection<Category> Categories { get; }
    [ObservableProperty] private string _repositoryInput = string.Empty;
    [ObservableProperty] private Category? _selectedCategory;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdd), nameof(HasPreview), nameof(PreviewTitle), nameof(PreviewDescription),
        nameof(PreviewOwner), nameof(PreviewRepo), nameof(PreviewStars), nameof(PreviewLicense))]
    private AppDetail? _previewDetail;

    // 扁平属性避免 XAML 深层绑定触发 AppDetail 的 required 成员激活。
    public bool HasPreview => PreviewDetail is not null;
    public string PreviewTitle => PreviewDetail?.App.DisplayName ?? string.Empty;
    public string PreviewDescription => PreviewDetail?.App.Description ?? string.Empty;
    public string PreviewOwner => PreviewDetail?.App.Owner ?? string.Empty;
    public string PreviewRepo => PreviewDetail?.App.Repo ?? string.Empty;
    public string PreviewStars => PreviewDetail?.App.Stars.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    public string PreviewLicense => PreviewDetail?.App.License ?? string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdd), nameof(IsEditable))]
    private bool _isBusy;
    [ObservableProperty] private string _errorMessage = string.Empty;
    public bool IsEditable => !IsBusy;
    public bool CanAdd => !_disposed && !IsBusy && PreviewDetail is not null
        && string.Equals(_previewedInput, RepositoryInput.Trim(), StringComparison.Ordinal);

    partial void OnRepositoryInputChanged(string value)
    {
        _version++;
        PreviewDetail = null;
        _previewedInput = null;
        ErrorMessage = string.Empty;
        OnPropertyChanged(nameof(CanAdd));
    }

    [RelayCommand]
    private async Task PreviewAsync(CancellationToken ct)
    {
        if (_disposed) return;
        var input = RepositoryInput.Trim();
        var version = ++_version;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        IsBusy = true;
        ErrorMessage = string.Empty;
        PreviewDetail = null;
        try
        {
            var detail = await _repositories.PreviewAsync(input, cancellation.Token);
            if (_disposed || cancellation.IsCancellationRequested || version != _version) return;
            _previewedInput = input;
            PreviewDetail = detail;
            // 预览只用于确认仓库身份；分类保持用户在对话框中的选择。
            SelectedCategory ??= Categories.FirstOrDefault(c => c.Id == detail.App.PrimaryCategory)
                ?? Categories.FirstOrDefault();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_disposed && version == _version) ErrorMessage = _catalog.FormatError("AddFailed", ex);
        }
        finally { if (!_disposed) IsBusy = false; }
    }

    public async Task<AppDetail?> AddAsync(CancellationToken ct = default)
    {
        if (!CanAdd)
        {
            ErrorMessage = Text["PreviewFirst"];
            return null;
        }
        var input = RepositoryInput.Trim();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            return await _repositories.AddAsync(input, SelectedCategory?.Id, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return null; }
        catch (Exception ex)
        {
            if (!_disposed) ErrorMessage = _catalog.FormatError("AddFailed", ex);
            return null;
        }
        finally { if (!_disposed) IsBusy = false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
