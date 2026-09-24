using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using GreenLuma_Manager.Models;
using GreenLuma_Manager.Services;

namespace GreenLuma_Manager.Controllers;

public class SearchController
{
    private readonly Button _btnAddAll;
    private readonly DataGrid _dgResults;
    private readonly GameListController? _gameListController;
    private readonly NotificationManager _notificationManager;
    private readonly UIElement _pnlEmptyResults;
    private readonly UIElement _pnlResultsHeader;
    private readonly UIElement _pnlSearchLoading;
    private CancellationTokenSource? _searchCts;

    public SearchController(
        DataGrid dgResults,
        UIElement pnlSearchLoading,
        UIElement pnlEmptyResults,
        UIElement pnlResultsHeader,
        Button btnAddAll,
        NotificationManager notificationManager,
        GameListController? gameListController = null)
    {
        _dgResults = dgResults;
        _pnlSearchLoading = pnlSearchLoading;
        _pnlEmptyResults = pnlEmptyResults;
        _pnlResultsHeader = pnlResultsHeader;
        _btnAddAll = btnAddAll;
        _notificationManager = notificationManager;
        _gameListController = gameListController;

        SearchResults = [];
        _dgResults.ItemsSource = SearchResults;
    }

    public int TotalResultCount { get; private set; }

    public ObservableCollection<Game> SearchResults { get; }

    public event Action<Game>? GameSelected;
    public event Action? ResultsLoaded;

    public async Task ExecuteSearchAsync(string query, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            _notificationManager.ShowToast("Enter a search term", false);
            return;
        }

        if (query.Length < 3)
        {
            _notificationManager.ShowToast("Search term must be at least 3 characters", false);
            return;
        }

        if (query.Length > 200)
        {
            _notificationManager.ShowToast("Search term is too long (max 200 characters)", false);
            return;
        }

        if (_searchCts != null)
        {
            await _searchCts.CancelAsync();
            _searchCts.Dispose();
        }

        _searchCts = new CancellationTokenSource();

        try
        {
            await PerformSearchAsync(query, _searchCts.Token);
        }
        catch (OperationCanceledException)
        {
            _notificationManager.StopLoadingDots();
        }
        catch (Exception ex)
        {
            _notificationManager.StopLoadingDots();
            _notificationManager.ShowToast("Search failed: " + ex.Message, false);
        }
    }

    private async Task PerformSearchAsync(string query, CancellationToken token)
    {
        Keyboard.ClearFocus();
        ShowLoading();

        var results = await Task.Run(() => SearchService.SearchAsync(query, ct: token), token);

        if (token.IsCancellationRequested) return;

        DisplayResults(results, query);

        if (results.Count == 0) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await SearchService.FetchIconUrlsAsync(results, () =>
                {
                    if (!token.IsCancellationRequested)
                        Application.Current.Dispatcher.Invoke(() => ResultsLoaded?.Invoke());
                });
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "SearchController.FetchIcons");
            }
            finally
            {
                if (!token.IsCancellationRequested)
                    Application.Current.Dispatcher.Invoke(() => ResultsLoaded?.Invoke());
            }
        }, token);
    }

    public void ShowLoading()
    {
        _dgResults.Visibility = Visibility.Collapsed;
        _pnlEmptyResults.Visibility = Visibility.Collapsed;
        _pnlSearchLoading.Visibility = Visibility.Visible;

        var fadeIn = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(250))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        _pnlSearchLoading.BeginAnimation(UIElement.OpacityProperty, fadeIn);

        if (_pnlSearchLoading.RenderTransform is ScaleTransform transform)
        {
            var scaleIn = new DoubleAnimation(0.95, 1.0, TimeSpan.FromMilliseconds(250))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            transform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleIn);
            transform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleIn);
        }

        _notificationManager.StartLoadingDots();
    }

    public void HideLoading()
    {
        _notificationManager.StopLoadingDots();
        _pnlSearchLoading.Visibility = Visibility.Collapsed;
    }

    public void DisplayResults(List<Game> results, string? query = null)
    {
        var existingSet = new HashSet<string>(_gameListController?.GetSelectedAppIds() ?? []);

        if (_gameListController != null && !string.IsNullOrWhiteSpace(query))
        {
            var q = query.Trim();
            var matchingProfileGames = _gameListController.Games
                .Where(pg => (pg.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || pg.AppId == q)
                             && !results.Any(r => r.AppId == pg.AppId))
                .ToList();

            for (var i = matchingProfileGames.Count - 1; i >= 0; i--)
            {
                var pg = matchingProfileGames[i];
                results.Insert(0, new Game
                {
                    AppId = pg.AppId,
                    Name = pg.Name,
                    Type = pg.Type,
                    IconUrl = pg.IconUrl,
                    IsInProfile = true
                });
            }
        }

        foreach (var game in results)
            game.IsInProfile = existingSet.Contains(game.AppId);

        results = [.. results.OrderByDescending(game => game.IsInProfile)];

        TotalResultCount = results.Count;
        SearchResults.Clear();
        HideLoading();

        _pnlResultsHeader.Visibility = Visibility.Visible;

        if (results.Count == 0)
        {
            _dgResults.Visibility = Visibility.Collapsed;
            _pnlEmptyResults.Visibility = Visibility.Visible;
            _btnAddAll.Visibility = Visibility.Collapsed;
            ResultsLoaded?.Invoke();
            return;
        }

        foreach (var game in results)
            SearchResults.Add(game);

        _dgResults.Visibility = Visibility.Visible;
        _pnlEmptyResults.Visibility = Visibility.Collapsed;
        _btnAddAll.Visibility = Visibility.Visible;
        ResultsLoaded?.Invoke();
    }

    public void SyncProfileStatus(IEnumerable<string> existingAppIds)
    {
        var set = new HashSet<string>(existingAppIds);
        foreach (var game in SearchResults)
            game.IsInProfile = set.Contains(game.AppId);

        ReorderResultsByProfileStatus();
    }

    private void ReorderResultsByProfileStatus()
    {
        var ordered = SearchResults.OrderByDescending(game => game.IsInProfile).ToList();
        for (var index = 0; index < ordered.Count; index++)
        {
            if (ReferenceEquals(SearchResults[index], ordered[index])) continue;

            var currentIndex = SearchResults.IndexOf(ordered[index]);
            SearchResults.Move(currentIndex, index);
        }
    }

    public void CancelSearch()
    {
        _searchCts?.Cancel();
    }

    public void OnSearchResultDoubleClick(Game game)
    {
        GameSelected?.Invoke(game);
    }
}
