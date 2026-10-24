using System.ComponentModel;
using GitHubStore.App.Services.Markdown;
using GitHubStore.App.ViewModels;
using GitHubStore.Core.Abstractions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;

namespace GitHubStore.App.Views;

public sealed partial class CatalogView : UserControl, IDisposable
{
    private bool _suppressSortChange;
    private CatalogViewModel? _subscribedViewModel;
    private bool _disposed;
    private bool _openingReadme;
    private bool _installConfirmationOpen;
    private bool _cardActionInProgress;
    private readonly CancellationTokenSource _viewLifetime = new();
    private ContentDialog? _readmeDialog;
    private CancellationTokenSource? _dialogLifetime;
    private ContentControl? _chineseReadmeHost;
    private ContentControl? _releaseNotesHost;
    private ContentControl? _readmeHost;
    private ContentControl? _aiSummaryHost;
    private ContentControl? _assetExplanationHost;
    private ContentControl? _releaseImpactHost;

    public CatalogView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnViewLoaded;
        Unloaded += OnViewUnloaded;
        ActualThemeChanged += OnActualThemeChanged;
    }

    private CatalogViewModel? ViewModel => DataContext as CatalogViewModel;

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        CloseReadme();
        AttachViewModel();
        PopulateSortSelector();
        UpdateSortSelection();
        UpdateFeedback();
    }

    private void AttachViewModel()
    {
        if (_disposed || ReferenceEquals(_subscribedViewModel, ViewModel)) return;
        DetachViewModel();
        _subscribedViewModel = ViewModel;
        if (_subscribedViewModel is not { } vm) return;
        vm.Text.LanguageChanged += OnLanguageChanged;
        vm.PropertyChanged += OnViewModelPropertyChanged;
        if (vm.Installation is not null) vm.Installation.PropertyChanged += OnOperationChanged;
        if (vm.Downloads is not null) vm.Downloads.PropertyChanged += OnOperationChanged;
    }

    private void DetachViewModel()
    {
        if (_subscribedViewModel is not { } vm) return;
        vm.Text.LanguageChanged -= OnLanguageChanged;
        vm.PropertyChanged -= OnViewModelPropertyChanged;
        if (vm.Installation is not null) vm.Installation.PropertyChanged -= OnOperationChanged;
        if (vm.Downloads is not null) vm.Downloads.PropertyChanged -= OnOperationChanged;
        _subscribedViewModel = null;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100", Justification = "WinUI lifecycle event; failures are caught at the boundary.")]
    private async void OnViewLoaded(object sender, RoutedEventArgs args)
    {
        try
        {
            AttachViewModel();
            PopulateSortSelector();
            UpdateSortSelection();
            UpdateFeedback();
            if (ViewModel is { } vm) await vm.RefreshCardsAsync(_viewLifetime.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ReportError(ex); }
    }

    private void OnViewUnloaded(object sender, RoutedEventArgs args)
    {
        CloseReadme();
        ViewModel?.CancelCards();
        DetachViewModel();
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args) => OnDetailChanged();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CloseReadme();
        ViewModel?.CancelCards();
        DetachViewModel();
        _ = CancelSafelyAsync(_viewLifetime);
        ClearReadmeHosts();
    }

    private static async Task CancelSafelyAsync(CancellationTokenSource? cancellation)
    {
        if (cancellation is null) return;
        try { await cancellation.CancelAsync(); }
        catch (Exception) { /* Service cancellation callbacks cannot escape visual lifecycle events. */ }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        UpdateFeedback();
        if (args.PropertyName == nameof(CatalogViewModel.SelectedDetail)) OnDetailChanged();
        else if (args.PropertyName == nameof(CatalogViewModel.DisplayReadmeMarkdown)) RenderMarkdown(_chineseReadmeHost, ViewModel?.DisplayReadmeMarkdown);
        else if (args.PropertyName == nameof(CatalogViewModel.ReadmeMarkdown)) RenderMarkdown(_readmeHost, ViewModel?.ReadmeMarkdown);
        else if (args.PropertyName == nameof(CatalogViewModel.AiSummaryMarkdown)) RenderMarkdown(_aiSummaryHost, ViewModel?.AiSummaryMarkdown);
        else if (args.PropertyName == nameof(CatalogViewModel.AssetExplanationMarkdown)) RenderMarkdown(_assetExplanationHost, ViewModel?.AssetExplanationMarkdown);
        else if (args.PropertyName == nameof(CatalogViewModel.ReleaseImpactMarkdown)) RenderMarkdown(_releaseImpactHost, ViewModel?.ReleaseImpactMarkdown);
    }

    private void OnOperationChanged(object? sender, PropertyChangedEventArgs args) => UpdateFeedback();

    private void UpdateFeedback()
    {
        if (_disposed) return;
        var vm = ViewModel;
        SearchFeedbackPanel.Visibility = vm is not null && (vm.IsAssistantSearchBusy || !string.IsNullOrWhiteSpace(vm.AssistantSearchStatus)
            || !string.IsNullOrWhiteSpace(vm.AssistantSearchError) || !string.IsNullOrWhiteSpace(vm.ErrorMessage)) ? Visibility.Visible : Visibility.Collapsed;
        var installation = vm?.Installation;
        InstallationPanel.Visibility = installation is not null && (installation.IsInstalling || !string.IsNullOrWhiteSpace(installation.StatusMessage)
            || !string.IsNullOrWhiteSpace(installation.ErrorMessage)) ? Visibility.Visible : Visibility.Collapsed;
        DiagnosisPanel.Visibility = installation?.LastDiagnostic is not null ? Visibility.Visible : Visibility.Collapsed;
        var downloads = vm?.Downloads;
        // 队列有任务或队列自身报错时同样要显示，否则用户看不到已排队/待恢复的任务。
        DownloadQueuePanel.Visibility = downloads is not null && (downloads.IsDownloading
            || downloads.HasQueueJobs || !string.IsNullOrWhiteSpace(downloads.QueueStatus)
            || !string.IsNullOrWhiteSpace(downloads.Status))
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnLanguageChanged(object? sender, EventArgs args)
    {
        PopulateSortSelector();
        UpdateSortSelection();
        OnDetailChanged();
        UpdateFeedback();
    }

    private void PopulateSortSelector()
    {
        var text = ViewModel?.Text;
        _suppressSortChange = true;
        try
        {
            SortSelector.Items.Clear();
            SortSelector.Items.Add(text?["SortStars"] ?? "Stars");
            SortSelector.Items.Add(text?["SortUpdated"] ?? "Recently updated");
            SortSelector.Items.Add(text?["SortName"] ?? "Name");
        }
        finally { _suppressSortChange = false; }
    }

    private void UpdateSortSelection()
    {
        if (ViewModel is not { } vm) return;
        _suppressSortChange = true;
        try { SortSelector.SelectedIndex = vm.Sort switch { SortKey.Updated => 1, SortKey.Name => 2, _ => 0 }; }
        finally { _suppressSortChange = false; }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100", Justification = "WinUI event; exceptions are caught.")]
    private async void OnSearchSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        try { if (ViewModel is { } vm) await vm.SearchCommand.ExecuteAsync(null); }
        catch (Exception ex) { ReportError(ex); }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100", Justification = "WinUI event; exceptions are caught.")]
    private async void OnSortChanged(object sender, SelectionChangedEventArgs args)
    {
        try
        {
            if (_suppressSortChange || !IsLoaded || ViewModel is not { } vm || sender is not ComboBox combo) return;
            vm.Sort = combo.SelectedIndex switch { 1 => SortKey.Updated, 2 => SortKey.Name, _ => SortKey.Stars };
            await vm.LoadAsync();
        }
        catch (Exception ex) { ReportError(ex); }
    }

    private void OnCardSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (sender is not Border { Child: Grid layout } || layout.ColumnDefinitions.Count != 3) return;
        var compact = args.NewSize.Width < 780;
        layout.ColumnDefinitions[0].Width = new GridLength(compact ? 150 : 220);
        layout.ColumnDefinitions[2].Width = new GridLength(compact ? 0 : 170);
        layout.ColumnSpacing = compact ? 12 : 20;
        foreach (var child in layout.Children.OfType<FrameworkElement>())
        {
            if (child.Name == "CardMetadata")
            {
                Grid.SetColumn(child, compact ? 1 : 2);
                Grid.SetRow(child, compact ? 2 : 0);
                Grid.SetRowSpan(child, compact ? 1 : 2);
                child.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            }
            else if (child.Name == "CardActions")
            {
                Grid.SetColumn(child, compact ? 1 : 2);
                Grid.SetRow(child, compact ? 3 : 2);
            }
            else if (child.Name == "CardPreview") Grid.SetRowSpan(child, compact ? 4 : 3);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100", Justification = "Lazy screenshot loading has an exception boundary.")]
    private void OnCardLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement element) _ = LoadCardImageAsync(element);
    }

    private void OnCardDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (sender.IsLoaded) _ = LoadCardImageAsync(sender);
    }

    private async Task LoadCardImageAsync(FrameworkElement element)
    {
        if (_disposed || element.DataContext is not CatalogCardViewModel card) return;
        if (FindScreenshotImage(element) is { } previous) previous.Source = null;
        try
        {
            await card.LoadScreenshotAsync(_viewLifetime.Token);
            if (!_disposed && ReferenceEquals(element.DataContext, card) && FindScreenshotImage(element) is { } image)
                image.Source = card.ScreenshotUri is { } uri ? new BitmapImage { DecodePixelWidth = 440, UriSource = uri } : null;
        }
        catch (OperationCanceledException) { }
        catch (Exception) { card.ClearScreenshot(); }
    }

    private static Image? FindScreenshotImage(DependencyObject parent)
    {
        if (parent is Image image) return image;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            if (FindScreenshotImage(VisualTreeHelper.GetChild(parent, index)) is { } found) return found;
        return null;
    }

    private void OnScreenshotFailed(object sender, ExceptionRoutedEventArgs args)
    {
        if (sender is FrameworkElement { DataContext: CatalogCardViewModel card }) card.ClearScreenshot();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100", Justification = "Explicit item activation; dialog failures are caught.")]
    private async void OnCardClick(object sender, ItemClickEventArgs args)
    {
        if (!_cardActionInProgress && args.ClickedItem is CatalogCardViewModel card) await ShowReadmeAsync(card, false);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100", Justification = "Explicit card action; dialog failures are caught.")]
    private async void OnCardMoreClick(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { DataContext: CatalogCardViewModel card }) await ShowReadmeAsync(card, true);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100", Justification = "Explicit card installation; all errors handled.")]
    private async void OnCardInstallClick(object sender, RoutedEventArgs args)
    {
        if (_disposed || _openingReadme || _installConfirmationOpen || _cardActionInProgress || ViewModel is not { } vm
            || sender is not FrameworkElement { DataContext: CatalogCardViewModel card }) return;
        _cardActionInProgress = true;
        try
        {
            await vm.SelectAsync(card.App, _viewLifetime.Token);
            if (vm.SelectedDetail?.App.Id != card.App.Id) return;
            if (vm.CanInstallSelected && vm.CreateInstallRequest() is { } request) await ConfirmInstallAsync(vm, request);
            else await ShowReadmeAsync(card, true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ReportError(ex); }
        finally { _cardActionInProgress = false; }
    }

    // Only explicit card activation opens this modal; loading or auto-selecting list data never does.
    private async Task ShowReadmeAsync(CatalogCardViewModel card, bool showActions)
    {
        if (_disposed || _openingReadme || _readmeDialog is not null || _installConfirmationOpen || ViewModel is not { } vm) return;
        _openingReadme = true;
        AppInstallRequest? pendingInstall = null;
        var pendingRemove = false;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_viewLifetime.Token);
        _dialogLifetime = lifetime;
        ContentDialog? dialog = null;
        TypedEventHandler<XamlRoot, XamlRootChangedEventArgs>? sizeChanged = null;
        try
        {
            await vm.SelectAsync(card.App, lifetime.Token);
            if (_disposed || lifetime.IsCancellationRequested || vm.SelectedDetail?.App.Id != card.App.Id || XamlRoot is null) return;
            var body = new Grid { RowSpacing = 10, DataContext = vm };
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            toolbar.Children.Add(BoundButton(nameof(vm.ChineseReadmeLoadLabel), nameof(vm.GenerateChineseReadmeCommand), "RegenerateChineseReadmeButton"));
            var cancel = BoundButton(nameof(vm.ChineseReadmeCancelLabel), nameof(vm.CancelChineseReadmeCommand), "CancelChineseReadmeButton");
            BindVisibility(cancel, nameof(vm.IsChineseReadmeBusy));
            toolbar.Children.Add(cancel);
            var original = new ToggleSwitch { VerticalAlignment = VerticalAlignment.Center };
            Bind(original, ToggleSwitch.HeaderProperty, nameof(vm.ChineseReadmeOriginalLabel));
            Bind(original, ToggleSwitch.IsOnProperty, nameof(vm.ShowOriginalReadme), BindingMode.TwoWay);
            Bind(original, ToggleSwitch.IsEnabledProperty, nameof(vm.HasOriginalReadme));
            toolbar.Children.Add(original);
            body.Children.Add(toolbar);
            var feedback = new StackPanel { Spacing = 5 };
            feedback.Children.Add(BoundText(nameof(vm.ChineseReadmeStatus), 12));
            feedback.Children.Add(BoundText(nameof(vm.ChineseReadmeError), 12, true));
            var translationProgress = new ProgressBar { IsIndeterminate = true };
            BindVisibility(translationProgress, nameof(vm.IsChineseReadmeBusy));
            feedback.Children.Add(translationProgress);
            var source = new HyperlinkButton { HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(0),
                Content = vm.Text.IsEnglish ? "Open source README" : "打开原始 README" };
            Bind(source, HyperlinkButton.NavigateUriProperty, nameof(vm.ChineseReadmeSourceUri));
            feedback.Children.Add(source);
            Grid.SetRow(feedback, 1);
            body.Children.Add(feedback);
            var document = new StackPanel { Spacing = 16 };
            var actions = BuildActions(vm, request => { pendingInstall = request; dialog?.Hide(); }, () => { pendingRemove = true; dialog?.Hide(); });
            actions.IsExpanded = showActions;
            document.Children.Add(actions);
            _chineseReadmeHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_chineseReadmeHost, "ChineseReadmeHost");
            document.Children.Add(_chineseReadmeHost);
            var reader = new ScrollViewer { Content = document, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled };
            Grid.SetRow(reader, 2);
            body.Children.Add(reader);
            dialog = new ContentDialog
            {
                XamlRoot = XamlRoot, RequestedTheme = ActualTheme, DataContext = vm,
                Title = card.DisplayName + " · " + vm.ChineseReadmeTitle, Content = body,
                CloseButtonText = vm.ChineseReadmeCloseLabel, DefaultButton = ContentDialogButton.Close,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(dialog, "ChineseReadmeDialog");
            void Resize()
            {
                if (dialog.XamlRoot is not { } root) return;
                body.Width = Math.Max(260, Math.Min(820, root.Size.Width - 120));
                body.Height = Math.Max(220, root.Size.Height - 240);
                dialog.Resources["ContentDialogMaxWidth"] = Math.Max(300, Math.Min(920, root.Size.Width - 40));
            }
            Resize();
            sizeChanged = (_, _) => Resize();
            dialog.XamlRoot.Changed += sizeChanged;
            _readmeDialog = dialog;
            dialog.Opened += (_, _) => { _ = LoadDialogReadmeAsync(vm, lifetime.Token); };
            dialog.Closing += (_, _) => vm.CancelChineseReadme();
            OnDetailChanged();
            await dialog.ShowAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ReportError(ex); }
        finally
        {
            if (dialog?.XamlRoot is { } root && sizeChanged is not null) root.Changed -= sizeChanged;
            vm.CancelChineseReadme();
            try { vm.CancelAiCommand.Execute(null); vm.CancelAssistantDetailCommand.Execute(null); } catch (Exception) { }
            if (ReferenceEquals(_readmeDialog, dialog)) _readmeDialog = null;
            if (ReferenceEquals(_dialogLifetime, lifetime)) _dialogLifetime = null;
            _ = CancelSafelyAsync(lifetime);
            _openingReadme = false;
            ClearReadmeHosts();
        }
        // A second dialog can only open once the README dialog has completely closed.
        if (!_disposed && pendingInstall is not null) await ConfirmInstallAsync(vm, pendingInstall);
        else if (!_disposed && pendingRemove)
        {
            try { await vm.RemoveSelectedAsync(_viewLifetime.Token); }
            catch (Exception ex) { ReportError(ex); }
        }
    }

    private async Task LoadDialogReadmeAsync(CatalogViewModel vm, CancellationToken ct)
    {
        try { await vm.LoadChineseReadmeForSelectedAsync(false, ct); }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!_disposed && !ct.IsCancellationRequested) vm.ChineseReadmeError = vm.Text.IsEnglish ? "Could not load README. Please retry." : "README 加载失败，请重试。"; }
    }

    private Expander BuildActions(CatalogViewModel vm, Action<AppInstallRequest> install, Action remove)
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(BoundText("Text[AssetSelectionPolicy]", 12));
        panel.Children.Add(BoundText(nameof(vm.AssetRecommendation), 12));
        var selector = new ComboBox { DisplayMemberPath = nameof(ReleaseAssetViewModel.SelectionLabel), HorizontalAlignment = HorizontalAlignment.Stretch };
        Bind(selector, ItemsControl.ItemsSourceProperty, nameof(vm.WindowsAssets));
        Bind(selector, ComboBox.SelectedItemProperty, nameof(vm.SelectedAsset), BindingMode.TwoWay);
        Bind(selector, ComboBox.HeaderProperty, "Text[AssetSelectionLabel]");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(selector, "AssetSelector");
        panel.Children.Add(selector);
        var download = BoundButton("Text[DownloadSelectedAsset]", null, "DownloadSelectedAssetButton");
        Bind(download, Button.IsEnabledProperty, nameof(vm.HasSelectedAsset));
        download.Click += OnDownloadSelectedAssetClick;
        var installButton = BoundButton("Text[InstallSelectedAsset]", null, "InstallSelectedAssetButton");
        Bind(installButton, Button.IsEnabledProperty, nameof(vm.CanInstallSelected));
        installButton.Click += (_, _) => { if (vm.CanInstallSelected && vm.CreateInstallRequest() is { } request) install(request); };
        panel.Children.Add(Row(download, installButton));
        panel.Children.Add(BoundText("Text[InstallModeHint]", 12));
        var noAssets = BoundText("Text[NoWindowsAssets]", 12);
        BindVisibility(noAssets, nameof(vm.HasNoWindowsAssets));
        panel.Children.Add(noAssets);
        panel.Children.Add(Row(BoundButton(nameof(vm.ExplainAssetLabel), nameof(vm.ExplainAssetCommand), "ExplainSelectedAssetButton"),
            BoundButton(nameof(vm.ExplainReleaseLabel), nameof(vm.ExplainReleaseCommand), "ExplainReleaseButton")));
        var cancelExplanation = BoundButton(nameof(vm.CancelAssistantLabel), nameof(vm.CancelAssistantDetailCommand), "CancelCatalogExplanationButton");
        BindVisibility(cancelExplanation, nameof(vm.IsAssistantDetailBusy));
        panel.Children.Add(cancelExplanation);
        var explanationProgress = new ProgressBar { IsIndeterminate = true };
        BindVisibility(explanationProgress, nameof(vm.IsAssistantDetailBusy));
        panel.Children.Add(explanationProgress);
        panel.Children.Add(BoundText(nameof(vm.AssistantDetailStatus), 12));
        panel.Children.Add(BoundText(nameof(vm.AssistantDetailError), 12, true));
        _assetExplanationHost = NewMarkdownHost();
        _releaseImpactHost = NewMarkdownHost();
        panel.Children.Add(_assetExplanationHost);
        panel.Children.Add(_releaseImpactHost);
        var summary = new StackPanel { Spacing = 8 };
        summary.Children.Add(BoundText("Text[AiSummaryNotice]", 12));
        var cancelSummary = BoundButton("Text[AiCancel]", nameof(vm.CancelAiCommand), "CancelReadmeSummaryButton");
        BindVisibility(cancelSummary, nameof(vm.IsAiBusy));
        summary.Children.Add(Row(BoundButton(nameof(vm.GenerateSummaryLabel), nameof(vm.GenerateSummaryCommand), "GenerateReadmeSummaryButton"), cancelSummary));
        var summaryProgress = new ProgressBar { IsIndeterminate = true };
        BindVisibility(summaryProgress, nameof(vm.IsAiBusy));
        summary.Children.Add(summaryProgress);
        summary.Children.Add(BoundText(nameof(vm.AiStatus), 12));
        summary.Children.Add(BoundText(nameof(vm.AiError), 12, true));
        _aiSummaryHost = NewMarkdownHost();
        summary.Children.Add(_aiSummaryHost);
        summary.Children.Add(BoundButton("Text[AiReadmeLoad]", nameof(vm.LoadReadmeCommand), "LoadReadmeButton"));
        _readmeHost = NewMarkdownHost();
        summary.Children.Add(_readmeHost);
        var summaryExpander = new Expander { Content = summary, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        Bind(summaryExpander, Expander.HeaderProperty, "Text[AiSummaryTitle]");
        panel.Children.Add(summaryExpander);
        _releaseNotesHost = NewMarkdownHost();
        var releaseExpander = new Expander { Content = _releaseNotesHost, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        Bind(releaseExpander, Expander.HeaderProperty, "Text[ReadmeSection]");
        panel.Children.Add(releaseExpander);
        var repository = new HyperlinkButton { HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(0) };
        Bind(repository, ContentControl.ContentProperty, "Text[OpenRepository]");
        Bind(repository, HyperlinkButton.NavigateUriProperty, nameof(vm.RepositoryUri));
        var releases = new HyperlinkButton { HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(0) };
        Bind(releases, ContentControl.ContentProperty, "Text[OpenReleases]");
        Bind(releases, HyperlinkButton.NavigateUriProperty, nameof(vm.ReleasesUri));
        panel.Children.Add(Row(repository, releases));
        panel.Children.Add(BoundText(nameof(vm.CacheStatusText), 12));
        if (vm.HasVerification)
        {
            panel.Children.Add(BoundText(nameof(vm.VerificationDateText), 12));
            panel.Children.Add(BoundText(nameof(vm.VerificationScopeText), 12));
        }
        foreach (var source in vm.RecommendationSources)
        {
            panel.Children.Add(new HyperlinkButton { Content = source.LinkText, NavigateUri = source.Url, Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left });
            panel.Children.Add(new TextBlock { Text = source.EvidenceText, TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        }
        var removeButton = BoundButton("Text[Remove]", null, "RemoveRepositoryButton");
        removeButton.Click += (_, _) => remove();
        panel.Children.Add(removeButton);
        panel.Children.Add(BoundText("Text[RemoveHint]", 12));
        return new Expander { Header = vm.Text.IsEnglish ? "Download, install and AI tools" : "下载、安装与 AI 助手", Content = panel,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    }

    private static StackPanel Row(params UIElement[] controls)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        foreach (var control in controls) panel.Children.Add(control);
        return panel;
    }

    private static ContentControl NewMarkdownHost() => new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private static TextBlock BoundText(string path, double size = 14, bool error = false)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = size };
        Bind(text, TextBlock.TextProperty, path);
        if (error) text.Foreground = new SolidColorBrush(Microsoft.UI.Colors.IndianRed);
        return text;
    }
    private static Button BoundButton(string labelPath, string? commandPath, string id)
    {
        var button = new Button { HorizontalAlignment = HorizontalAlignment.Left };
        Bind(button, ContentControl.ContentProperty, labelPath);
        if (commandPath is not null) Bind(button, Button.CommandProperty, commandPath);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(button, id);
        return button;
    }
    private static void Bind(FrameworkElement target, DependencyProperty property, string path, BindingMode mode = BindingMode.OneWay)
        => target.SetBinding(property, new Binding { Path = new PropertyPath(path), Mode = mode });
    private static void BindVisibility(FrameworkElement target, string path)
        => target.SetBinding(VisibilityProperty, new Binding { Path = new PropertyPath(path),
            Converter = (IValueConverter)Application.Current.Resources["BooleanToVisibility"] });

    private void CloseReadme()
    {
        _subscribedViewModel?.CancelChineseReadme();
        _ = CancelSafelyAsync(_dialogLifetime);
        try { _readmeDialog?.Hide(); } catch (Exception) { }
    }
    private void ClearReadmeHosts()
    {
        foreach (var host in new[] { _chineseReadmeHost, _releaseNotesHost, _readmeHost, _aiSummaryHost, _assetExplanationHost, _releaseImpactHost })
            if (host is not null) host.Content = null;
        _chineseReadmeHost = _releaseNotesHost = _readmeHost = _aiSummaryHost = _assetExplanationHost = _releaseImpactHost = null;
    }

    internal void RenderReleaseNotes(string? markdown) => RenderMarkdown(_releaseNotesHost, markdown);
    private static void RenderMarkdown(ContentControl? host, string? markdown)
    {
        if (host is null) return;
        try { host.Content = string.IsNullOrWhiteSpace(markdown) ? null : MarkdownRenderer.Render(markdown); }
        catch (Exception)
        {
            host.Content = new TextBlock { Text = markdown is { Length: > 64000 } ? markdown[..64000] + "…" : markdown,
                TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        }
    }
    private void OnDetailChanged()
    {
        RenderReleaseNotes(ViewModel?.ReleaseNotes);
        RenderMarkdown(_chineseReadmeHost, ViewModel?.DisplayReadmeMarkdown);
        RenderMarkdown(_readmeHost, ViewModel?.ReadmeMarkdown);
        RenderMarkdown(_aiSummaryHost, ViewModel?.AiSummaryMarkdown);
        RenderMarkdown(_assetExplanationHost, ViewModel?.AssetExplanationMarkdown);
        RenderMarkdown(_releaseImpactHost, ViewModel?.ReleaseImpactMarkdown);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100", Justification = "Download event; exceptions caught.")]
    private async void OnDownloadSelectedAssetClick(object sender, RoutedEventArgs args)
    {
        try { if (ViewModel is { Downloads: { } downloads, SelectedAsset: { } selected }) await downloads.DownloadAsync(selected.Asset); }
        catch (Exception ex) { ReportError(ex); }
    }

    private async Task ConfirmInstallAsync(CatalogViewModel vm, AppInstallRequest request)
    {
        if (_disposed || _installConfirmationOpen || vm.Installation is not { } installation || installation.IsInstalling) return;
        _installConfirmationOpen = true;
        try
        {
            var target = installation.GetTargetDirectory(request.App);
            var content = new StackPanel { Spacing = 12, MaxWidth = 540 };
            content.Children.Add(new TextBlock { Text = request.App.DisplayName + " · " + request.Release.TagName, TextWrapping = TextWrapping.Wrap });
            content.Children.Add(new TextBlock { Text = request.Asset.Name, TextWrapping = TextWrapping.Wrap });
            content.Children.Add(new TextBlock { Text = vm.Text["InstallTargetLabel"] + "\n" + target, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            content.Children.Add(new TextBlock { Text = vm.Text["InstallConfirmHint"], TextWrapping = TextWrapping.Wrap });
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot, RequestedTheme = ActualTheme, Title = vm.Text["InstallConfirmTitle"], Content = content,
                PrimaryButtonText = vm.Text["InstallConfirmAction"], CloseButtonText = vm.Text["Cancel"], DefaultButton = ContentDialogButton.Close,
            };
            var approved = false;
            var cancelled = false;
            dialog.PrimaryButtonClick += (_, _) => approved = true;
            dialog.CloseButtonClick += (_, _) => cancelled = true;
            await dialog.ShowAsync();
            if (approved && !cancelled && !_disposed) await installation.InstallAsync(request, ChooseExecutableAsync);
        }
        catch (Exception) { if (!_disposed) vm.ErrorMessage = vm.Text["InstallFailed"]; }
        finally { _installConfirmationOpen = false; }
    }

    private Task<string?> ChooseExecutableAsync(IReadOnlyList<string> candidates, CancellationToken ct)
    {
        if (_disposed || ct.IsCancellationRequested) return Task.FromResult<string?>(null);
        var result = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(() => { _ = ShowExecutablePickerAsync(candidates, ct, result); })) result.TrySetResult(null);
        return result.Task;
    }
    private async Task ShowExecutablePickerAsync(IReadOnlyList<string> candidates, CancellationToken ct, TaskCompletionSource<string?> result)
    {
        try
        {
            if (_disposed || ct.IsCancellationRequested || ViewModel is not { } vm) { result.TrySetResult(null); return; }
            var picker = new ComboBox { ItemsSource = candidates, HorizontalAlignment = HorizontalAlignment.Stretch,
                PlaceholderText = vm.Text["InstallChooseEntry"], MinWidth = 320 };
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot, RequestedTheme = ActualTheme, Title = vm.Text["InstallChooseEntry"], Content = picker,
                PrimaryButtonText = vm.Text["InstallRegisterAction"], CloseButtonText = vm.Text["Cancel"], IsPrimaryButtonEnabled = false, DefaultButton = ContentDialogButton.Close,
            };
            picker.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = picker.SelectedItem is string;
            var approved = false;
            var cancelled = false;
            dialog.PrimaryButtonClick += (_, _) => approved = true;
            dialog.CloseButtonClick += (_, _) => cancelled = true;
            using var registration = ct.Register(() => { DispatcherQueue.TryEnqueue(() => dialog.Hide()); });
            await dialog.ShowAsync();
            result.TrySetResult(!ct.IsCancellationRequested && approved && !cancelled ? picker.SelectedItem as string : null);
        }
        catch (Exception) { result.TrySetResult(null); }
    }
    private void ReportError(Exception ex)
    {
        if (!_disposed && ViewModel is { } vm) vm.ErrorMessage = vm.FormatError("ActionFailed", ex);
    }
}
