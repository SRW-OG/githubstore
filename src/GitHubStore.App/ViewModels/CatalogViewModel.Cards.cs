using System.Collections.ObjectModel;
using GitHubStore.App.Services.Ai;
using GitHubStore.Core.Abstractions;
using CatalogApp = GitHubStore.Core.Abstractions.App;

namespace GitHubStore.App.ViewModels;

public sealed partial class CatalogViewModel
{
    private readonly SemaphoreSlim _cardScreenshotGate = new(3, 3);
    private CancellationTokenSource? _cardsCancellation;
    private int _cardsVersion;
    public ObservableCollection<CatalogCardViewModel> Cards { get; } = [];

    /// <summary>Project the current list from local detail snapshots. Never fetches README or calls a model.</summary>
    internal async Task RefreshCardsAsync(CancellationToken ct = default)
    {
        if (_disposed) return;
        CancelCards();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        _cardsCancellation = cancellation;
        var token = cancellation.Token;
        var version = _cardsVersion;
        var loadVersion = _loadVersion;
        var language = Text.LanguageCode;
        var snapshot = Apps.ToArray();
        var cards = new List<CatalogCardViewModel>(snapshot.Length);
        try
        {
            foreach (var app in snapshot)
            {
                if (!IsCurrent()) return;
                AppDetail detail;
                try
                {
                    // The actual GitHub catalog implements GetAppAsync as a local immutable snapshot lookup.
                    detail = await _catalog.GetAppAsync(app.Id, token).WaitAsync(token);
                    if (detail.App.Id != app.Id) detail = new AppDetail { App = app };
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (Exception) { detail = new AppDetail { App = app }; }
                if (!IsCurrent()) return;
                cards.Add(new CatalogCardViewModel(detail, Text.IsEnglish,
                    _readmeSummary is null ? null : screenshotToken => LoadCardScreenshotAsync(detail.App, screenshotToken), token));
            }
            if (!IsCurrent()) return;
            Cards.Clear();
            foreach (var card in cards) Cards.Add(card);
            cards.Clear(); // Ownership transfers to Cards until the next navigation or projection.
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { /* Optional card enrichment must not fail a list or language change. */ }
        finally
        {
            foreach (var card in cards) card.Dispose();
        }
        bool IsCurrent() => !_disposed && !token.IsCancellationRequested
            && version == _cardsVersion && loadVersion == _loadVersion
            && ReferenceEquals(_cardsCancellation, cancellation) && language == Text.LanguageCode
            && snapshot.SequenceEqual(Apps);
    }

    private async Task<Uri?> LoadCardScreenshotAsync(CatalogApp app, CancellationToken ct)
    {
        if (_readmeSummary is null || _disposed) return null;
        await _cardScreenshotGate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (_disposed) return null;
            var readme = await _readmeSummary.LoadReadmeAsync(app, ct).WaitAsync(ct);
            ct.ThrowIfCancellationRequested();
            return ReadmeScreenshotExtractor.Extract(app, readme.Markdown, 3, readme.SourceUrl)
                .Select(CatalogCardViewModel.SafeImageUri).FirstOrDefault(uri => uri is not null);
        }
        finally { _cardScreenshotGate.Release(); }
    }

    /// <summary>Invalidate pending projection/images at navigation start, unload, and disposal.</summary>
    internal void CancelCards()
    {
        ++_cardsVersion;
        var cancellation = _cardsCancellation;
        _cardsCancellation = null;
        foreach (var card in Cards) card.Dispose();
        if (cancellation is not null) _ = CancelCardScopeAsync(cancellation);
    }

    private static async Task CancelCardScopeAsync(CancellationTokenSource cancellation)
    {
        try { await cancellation.CancelAsync(); }
        catch (Exception) { /* Image discovery must not block navigation. */ }
        finally { cancellation.Dispose(); }
    }
}
