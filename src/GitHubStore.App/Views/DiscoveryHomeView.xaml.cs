using System.ComponentModel;
using GitHubStore.App.Services.Markdown;
using GitHubStore.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GitHubStore.App.Views;

public sealed partial class DiscoveryHomeView : UserControl, IDisposable
{
    private readonly Dictionary<ContentControl, (DiscoveryEntryViewModel Entry, PropertyChangedEventHandler Handler)> _reviewHosts = [];
    private bool _disposed;
    public DiscoveryHomeView()
    {
        InitializeComponent();
        Loaded += OnViewLoaded;
        Unloaded += OnViewUnloaded;
        DataContextChanged += OnDataContextChanged;
    }
    private DiscoveryHomeViewModel? ViewModel => DataContext as DiscoveryHomeViewModel;

    private void OnViewLoaded(object sender, RoutedEventArgs args) => UpdateSectionLayout();
    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        DetachReviewHosts();
        UpdateSectionLayout();
    }
    private void OnViewUnloaded(object sender, RoutedEventArgs args) => DetachReviewHosts();
    private void OnViewSizeChanged(object sender, SizeChangedEventArgs args) => UpdateSectionLayout();
    private void UpdateSectionLayout()
    {
        if (_disposed || SectionsGrid is null) return;
        var wide = ActualWidth >= 900;
        SectionsGrid.ColumnDefinitions[1].Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(WeeklySection, wide ? 1 : 0);
        Grid.SetRow(WeeklySection, wide ? 0 : 1);
        SectionsGrid.ColumnSpacing = wide ? 24 : 0;
    }
    private void OnReviewHostLoaded(object sender, RoutedEventArgs args)
    {
        if (_disposed || sender is not ContentControl { DataContext: DiscoveryEntryViewModel entry } host) return;
        DetachReviewHost(host);
        PropertyChangedEventHandler handler = (_, changed) =>
        {
            if (!_disposed && ReferenceEquals(host.DataContext, entry) && changed.PropertyName == nameof(DiscoveryEntryViewModel.AiReviewMarkdown))
                RenderReview(host, entry.AiReviewMarkdown);
        };
        _reviewHosts[host] = (entry, handler);
        entry.PropertyChanged += handler;
        RenderReview(host, entry.AiReviewMarkdown);
    }
    private void OnReviewHostUnloaded(object sender, RoutedEventArgs args)
    { if (sender is ContentControl host) DetachReviewHost(host); }
    private void DetachReviewHost(ContentControl host)
    {
        if (_reviewHosts.Remove(host, out var subscription)) subscription.Entry.PropertyChanged -= subscription.Handler;
        host.Content = null;
    }
    private void DetachReviewHosts()
    {
        foreach (var host in _reviewHosts.Keys.ToArray()) DetachReviewHost(host);
    }
    private static void RenderReview(ContentControl host, string markdown)
    {
        try { host.Content = string.IsNullOrWhiteSpace(markdown) ? null : MarkdownRenderer.Render(markdown); }
        catch (Exception)
        { host.Content = new TextBlock { Text = markdown, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }; }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Loaded -= OnViewLoaded;
        Unloaded -= OnViewUnloaded;
        DataContextChanged -= OnDataContextChanged;
        DetachReviewHosts();
    }
}
