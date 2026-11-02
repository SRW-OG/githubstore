using GitHubStore.Core.Abstractions;
using GitHubStore.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace GitHubStore.App.Views;

/// <summary>Launcher tab inside the main window. Theme cascades from the shell; only the registry subscription is owned here.</summary>
public sealed partial class LauncherView : UserControl, IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private IManagedAppLauncher? _launcher;
    private bool _loadedOnce;
    private bool _disposed;
    private bool _manualBusy;
    private CancellationTokenSource? _manualCancellation;
    private ContentDialog? _manualDialog;

    public LauncherView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public event EventHandler? ExploreRequested;

    private void OnExploreRequested(object sender, RoutedEventArgs args) => ExploreRequested?.Invoke(this, EventArgs.Empty);

    private LauncherViewModel? ViewModel => DataContext as LauncherViewModel;

    /// <summary>Subscribes to install-registry changes so new registrations appear without a manual refresh.</summary>
    public void Attach(IManagedAppLauncher launcher)
    {
        if (_disposed || _launcher is not null) return;
        _launcher = launcher;
        launcher.Changed += OnRegistryChanged;
    }

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        CancelManualInteraction();
        StartLoad();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100", Justification = "WinUI 事件必须返回 void；失败由视图模型的状态文本呈现。")]
    private async void OnLoaded(object sender, RoutedEventArgs args) => await StartLoadAsync();

    private void OnUnloaded(object sender, RoutedEventArgs args) => CancelManualInteraction();

    private void CancelManualInteraction()
    {
        _manualCancellation?.Cancel();
        _manualDialog?.Hide();
    }

    private void StartLoad() { if (IsLoaded) _ = StartLoadAsync(); }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100", Justification = "WinUI 事件必须返回 void；失败由视图模型的状态文本呈现。")]
    private async Task StartLoadAsync()
    {
        if (_loadedOnce || _disposed || ViewModel is not { } viewModel) return;
        _loadedOnce = true;
        await viewModel.RefreshEntriesAsync(_lifetime.Token);
    }

    private void OnRegistryChanged(object? sender, EventArgs args)
    {
        var dispatcher = DispatcherQueue;
        if (_disposed || dispatcher is null) return;
        dispatcher.TryEnqueue(RefreshAfterRegistryChanged);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100", Justification = "Dispatcher 边界；刷新失败由视图模型呈现。")]
    private async void RefreshAfterRegistryChanged()
    {
        if (!_disposed && ViewModel is { } viewModel) await viewModel.RefreshEntriesAsync(_lifetime.Token);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100", Justification = "WinUI 项点击边界；启动结果由视图模型呈现。")]
    private async void OnApplicationClick(object sender, ItemClickEventArgs args)
    {
        if (!_disposed && !_manualBusy && args.ClickedItem is LauncherItemViewModel app && ViewModel is { } viewModel)
            await viewModel.LaunchEntryAsync(app, _lifetime.Token);
    }

    private static bool IsSupportedManualFile(IStorageItem item) => item is StorageFile
        && Path.IsPathFullyQualified(item.Path)
        && !item.Path.StartsWith(@"\\", StringComparison.Ordinal)
        && (string.Equals(Path.GetExtension(item.Path), ".exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetExtension(item.Path), ".lnk", StringComparison.OrdinalIgnoreCase));

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100", Justification = "WinUI event boundary; the shared manual flow catches failures.")]
    private async void OnAddManualClick(object sender, RoutedEventArgs args)
    {
        await RunManualInteractionAsync(async (viewModel, ct) =>
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".exe");
            picker.FileTypeFilter.Add(".lnk");
            var handle = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, handle);
            var file = await picker.PickSingleFileAsync().AsTask(ct);
            ct.ThrowIfCancellationRequested();
            if (file is not null && IsSupportedManualFile(file))
                await AddManualFilesAsync(viewModel, [file], ct);
        });
    }

    private void OnManualDragOver(object sender, DragEventArgs args)
    {
        args.Handled = true;
        // Negotiate without opening files: shell sources may only expose storage
        // items after Drop. Validate file types and paths at that boundary.
        args.AcceptedOperation = !_disposed && !_manualBusy && IsLoaded
            && args.DataView.Contains(StandardDataFormats.StorageItems)
                ? DataPackageOperation.Copy : DataPackageOperation.None;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100", Justification = "WinUI drop boundary; the shared manual flow catches failures.")]
    private async void OnManualDrop(object sender, DragEventArgs args)
    {
        args.Handled = true;
        if (!args.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var deferral = args.GetDeferral();
        var dropCompleted = false;
        try
        {
            await RunManualInteractionAsync(async (viewModel, ct) =>
            {
                var items = await args.DataView.GetStorageItemsAsync().AsTask(ct);
                ct.ThrowIfCancellationRequested();
                var files = items.Where(IsSupportedManualFile).Cast<StorageFile>().ToArray();
                // Release the source drag before asking the user to confirm each entry.
                deferral.Complete();
                dropCompleted = true;
                await AddManualFilesAsync(viewModel, files, ct);
            });
        }
        finally { if (!dropCompleted) deferral.Complete(); }
    }

    private async Task RunManualInteractionAsync(Func<LauncherViewModel, CancellationToken, Task> action)
    {
        if (_disposed || _manualBusy || !IsLoaded || ViewModel is not { } viewModel) return;
        _manualBusy = true;
        AddManualButton.IsEnabled = false;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _manualCancellation = cancellation;
        try { await action(viewModel, cancellation.Token); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_disposed && !cancellation.IsCancellationRequested && ReferenceEquals(ViewModel, viewModel))
                viewModel.ReportManualError(ex);
        }
        finally
        {
            _manualDialog = null;
            _manualCancellation = null;
            _manualBusy = false;
            if (!_disposed) AddManualButton.IsEnabled = true;
        }
    }

    private async Task AddManualFilesAsync(LauncherViewModel viewModel, IReadOnlyList<StorageFile> files, CancellationToken ct)
    {
        if (files.Count == 0) return;
        var categories = await viewModel.GetManualCategoriesAsync(ct);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (!await ShowEntryDialogAsync(viewModel, file.Path, categories, ct)) break;
        }
    }

    private sealed record ManualCategoryChoice(CategoryId? Id, string Label);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD003", Justification = "The completion task is created and completed by this dialog's UI event, not foreign work. Awaiting it keeps the flow locked until cancellation unwinds; WinUI event handlers resume on the UI context without a JoinableTask dependency.")]
    private async Task<bool> ShowEntryDialogAsync(LauncherViewModel viewModel, string path,
        IReadOnlyList<Category> categories, CancellationToken ct, LauncherItemViewModel? item = null)
    {
        ct.ThrowIfCancellationRequested();
        var originalName = Path.GetFileNameWithoutExtension(path);
        var isEditing = item is not null;
        var canEditPath = item is { IsManual: true };
        var arguments = item?.Options.Arguments ?? string.Empty;
        string resolutionError = string.Empty;
        if (!isEditing || (canEditPath && string.Equals(Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var target = await viewModel.ResolveProgramAsync(path, ct);
                ct.ThrowIfCancellationRequested();
                path = target.Path;
                arguments = string.IsNullOrWhiteSpace(arguments)
                    ? target.Arguments
                    : string.IsNullOrWhiteSpace(target.Arguments) ? arguments : $"{target.Arguments} {arguments}";
            }
            catch (LauncherException ex) when (isEditing)
            {
                // A broken old shortcut must still allow the user to browse for a replacement.
                viewModel.ReportManualError(ex);
                resolutionError = viewModel.ErrorMessage;
            }
        }
        var nameInput = new TextBox
        {
            Header = viewModel.Text["LauncherManualName"],
            Text = item?.DisplayName ?? originalName,
        };
        AutomationProperties.SetAutomationId(nameInput, isEditing ? "LauncherPropertiesNameInput" : "LauncherManualNameInput");
        var choices = new List<ManualCategoryChoice> { new(null, viewModel.Text["LauncherUncategorized"]) };
        choices.AddRange(categories.Select(category => new ManualCategoryChoice(category.Id, viewModel.Text[$"category.{category.Id.Value}"])));
        if (item?.Category is { } currentCategory && choices.All(choice => choice.Id != currentCategory))
            choices.Add(new ManualCategoryChoice(currentCategory, viewModel.Text[$"category.{currentCategory.Value}"]));
        var categoryInput = new ComboBox
        {
            Header = viewModel.Text["SelectCategory"],
            ItemsSource = choices,
            DisplayMemberPath = nameof(ManualCategoryChoice.Label),
            SelectedItem = choices.First(choice => choice.Id == item?.Category),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetAutomationId(categoryInput, isEditing ? "LauncherPropertiesCategoryInput" : "LauncherManualCategoryInput");
        var pathText = new TextBlock { Text = path, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        AutomationProperties.SetAutomationId(pathText, "LauncherManualPathText");
        var pathInput = new TextBox
        {
            Header = viewModel.Text["LauncherManualPath"],
            Text = path,
            IsReadOnly = !canEditPath,
            TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetAutomationId(pathInput, "LauncherPropertiesPathInput");
        var choosePathButton = new Button { Content = viewModel.Text["LauncherChoosePath"] };
        AutomationProperties.SetAutomationId(choosePathButton, "LauncherPropertiesChoosePathButton");
        var error = new TextBlock
        {
            Text = resolutionError,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
        };

        var argumentsInput = new TextBox { Header = viewModel.Text["LauncherArguments"], Text = arguments };
        AutomationProperties.SetAutomationId(argumentsInput, isEditing ? "LauncherPropertiesArgumentsInput" : "LauncherManualArgumentsInput");
        var pathEditor = canEditPath
            ? new StackPanel { Spacing = 8, Children = { pathInput, choosePathButton } }
            : (FrameworkElement)pathInput;
        var fields = new StackPanel { Spacing = 10, MinWidth = 380 };
        if (isEditing)
        {
            fields.Children.Add(pathEditor);
        }
        else
        {
            fields.Children.Add(nameInput);
            fields.Children.Add(pathText);
            fields.Children.Add(categoryInput);
        }
        fields.Children.Add(argumentsInput);
        fields.Children.Add(error);
        var content = new ScrollViewer { Content = fields, MaxHeight = 560, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = viewModel.Text[isEditing ? "LauncherProperties" : "LauncherAddManual"],
            PrimaryButtonText = viewModel.Text[isEditing ? "LauncherSaveProperties" : "LauncherManualConfirm"],
            CloseButtonText = viewModel.Text["Cancel"],
            DefaultButton = ContentDialogButton.Primary,
            Content = content,
        };
        AutomationProperties.SetAutomationId(dialog, isEditing ? "LauncherPropertiesDialog" : "LauncherAddManualDialog");
        var submitting = false;
        var pickingPath = false;
        var added = false;
        TaskCompletionSource? submissionCompletion = null;
        TaskCompletionSource? pickerCompletion = null;
        choosePathButton.Click += OnChoosePathClick;
        dialog.PrimaryButtonClick += OnPrimaryClick;
        dialog.CloseButtonClick += OnCloseClick;
        dialog.Closing += OnClosing;
        _manualDialog = dialog;
        try
        {
            await dialog.ShowAsync();
            ct.ThrowIfCancellationRequested();
            return added;
        }
        finally
        {
            // Hide/cancel can finish ShowAsync before the service has observed cancellation.
            // Keep the shared flow locked until the pending submission has unwound.
            if (submissionCompletion is not null) await submissionCompletion.Task;
            if (pickerCompletion is not null) await pickerCompletion.Task;
            choosePathButton.Click -= OnChoosePathClick;
            dialog.PrimaryButtonClick -= OnPrimaryClick;
            dialog.CloseButtonClick -= OnCloseClick;
            dialog.Closing -= OnClosing;
            if (ReferenceEquals(_manualDialog, dialog)) _manualDialog = null;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100", Justification = "WinUI picker event; cancellation and errors are handled inside the current dialog.")]
        async void OnChoosePathClick(object sender, RoutedEventArgs args)
        {
            if (!canEditPath || submitting || pickingPath || ct.IsCancellationRequested) return;
            pickingPath = true;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            pickerCompletion = completion;
            choosePathButton.IsEnabled = false;
            dialog.IsPrimaryButtonEnabled = false;
            error.Text = string.Empty;
            try
            {
                var picker = new FileOpenPicker();
                picker.FileTypeFilter.Add(".exe");
                picker.FileTypeFilter.Add(".lnk");
                var handle = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, handle);
                var file = await picker.PickSingleFileAsync().AsTask(ct);
                ct.ThrowIfCancellationRequested();
                if (file is not null && IsSupportedManualFile(file))
                {
                    var target = await viewModel.ResolveProgramAsync(file.Path, ct);
                    ct.ThrowIfCancellationRequested();
                    pathInput.Text = target.Path;
                    argumentsInput.Text = target.Arguments;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                {
                    viewModel.ReportManualError(ex);
                    error.Text = viewModel.ErrorMessage;
                }
            }
            finally
            {
                pickingPath = false;
                choosePathButton.IsEnabled = true;
                dialog.IsPrimaryButtonEnabled = true;
                completion.TrySetResult();
            }
        }

        void OnCloseClick(ContentDialog sender, ContentDialogButtonClickEventArgs args) => _manualCancellation?.Cancel();

        void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
        {
            if (!added) _manualCancellation?.Cancel();
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100", Justification = "ContentDialog event with deferral; all failures are shown inside the dialog.")]
        async void OnPrimaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            args.Cancel = true;
            if (submitting || pickingPath || ct.IsCancellationRequested) return;
            var deferral = args.GetDeferral();
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            submissionCompletion = completion;
            submitting = true;
            dialog.IsPrimaryButtonEnabled = false;
            nameInput.IsEnabled = false;
            categoryInput.IsEnabled = false;
            pathInput.IsEnabled = false;
            argumentsInput.IsEnabled = false;
            choosePathButton.IsEnabled = false;
            error.Text = string.Empty;
            try
            {
                var category = (categoryInput.SelectedItem as ManualCategoryChoice)?.Id;
                added = item is null
                    ? await viewModel.AddManualEntryAsync(path, nameInput.Text, category, argumentsInput.Text, ct)
                    : await viewModel.SaveLaunchPropertiesAsync(item,
                        canEditPath ? pathInput.Text : path, argumentsInput.Text, ct);
                ct.ThrowIfCancellationRequested();
                args.Cancel = !added;
                if (!added) error.Text = viewModel.ErrorMessage;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                {
                    viewModel.ReportManualError(ex);
                    error.Text = viewModel.ErrorMessage;
                }
            }
            finally
            {
                submitting = false;
                dialog.IsPrimaryButtonEnabled = true;
                nameInput.IsEnabled = true;
                categoryInput.IsEnabled = true;
                pathInput.IsEnabled = true;
                argumentsInput.IsEnabled = true;
                choosePathButton.IsEnabled = true;
                deferral.Complete();
                completion.TrySetResult();
            }
        }
    }

    private void OnEntryMenuLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is MenuFlyoutItem menu && ViewModel is { } viewModel)
            menu.Text = viewModel.Text[menu.Name == "LaunchEntryMenuItem" ? "LauncherLaunch" : "LauncherProperties"];
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100", Justification = "WinUI flyout command; the shared interaction flow catches failures.")]
    private async void OnLaunchEntryMenuClick(object sender, RoutedEventArgs args)
    {
        // Flyout commands carry their own item; they never select or invoke the GridView tile.
        if (sender is FrameworkElement { Tag: LauncherItemViewModel item })
            await RunManualInteractionAsync((viewModel, ct) => viewModel.LaunchEntryAsync(item, ct));
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100", Justification = "WinUI flyout command; the shared interaction flow catches failures.")]
    private async void OnEntryPropertiesClick(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: LauncherItemViewModel item }) return;
        await RunManualInteractionAsync(async (viewModel, ct) =>
        {
            await ShowEntryDialogAsync(viewModel, item.ExecutablePath, [], ct, item);
        });
    }

    private void OnRemoveManualMenuLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is MenuFlyoutItem item && ViewModel is { } viewModel)
            item.Text = viewModel.Text["LauncherRemoveManual"];
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100", Justification = "WinUI menu boundary; the shared manual flow catches failures.")]
    private async void OnRemoveManualClick(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: LauncherItemViewModel { IsManual: true } item })
            await RunManualInteractionAsync((viewModel, ct) => viewModel.RemoveManualEntryAsync(item, ct));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelManualInteraction();
        if (_launcher is not null) _launcher.Changed -= OnRegistryChanged;
        _lifetime.Cancel();
        ApplicationScroller.Content = null;
        _lifetime.Dispose();
    }
}
