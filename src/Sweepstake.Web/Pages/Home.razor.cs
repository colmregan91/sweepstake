using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Sweepstake.Core;
using Sweepstake.Web.Components;

namespace Sweepstake.Web.Pages;

public sealed partial class Home : IAsyncDisposable
{
    /// <summary>
    /// The <c>*/15</c> schedule in .github/workflows/update-stats.yml. The page looks on the
    /// same cadence as the workflow so the footer can count down to a real event rather than
    /// to a phase set by whenever the tab happened to load.
    /// </summary>
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How long after each quarter-hour to look. The workflow still has to fetch, commit and
    /// deploy after the cron fires, and GitHub starts it late as often as not, so looking
    /// exactly on the boundary would usually just re-read the previous build.
    /// </summary>
    private static readonly TimeSpan CheckOffset = TimeSpan.FromMinutes(2);

    /// <summary>How long a changed value stays highlighted.</summary>
    private static readonly TimeSpan HighlightDuration = TimeSpan.FromSeconds(8);

    private readonly CancellationTokenSource _shutdown = new();
    private readonly HashSet<string> _changedGoals = new(StringComparer.Ordinal);
    private readonly HashSet<string> _changedAssists = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _goalRankMoves = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _assistRankMoves = new(StringComparer.Ordinal);

    private PicksFile? _picks;
    private StatsFile? _stats;
    private IReadOnlyList<ScoredEntrant> _goals = [];
    private IReadOnlyList<ScoredEntrant> _assists = [];

    private bool _loaded;
    private bool _haveRealStats;
    private string? _loadError;
    private bool _tabVisible = true;
    private int _highlightGeneration;
    private DateTimeOffset _nextCheckUtc = NextCheckAfter(DateTimeOffset.UtcNow);
    private bool _checking;

    private IJSObjectReference? _visibility;
    private DotNetObjectReference<Home>? _self;

    [Inject]
    private HttpClient Http { get; set; } = default!;

    [Inject]
    private IJSRuntime Js { get; set; } = default!;

    private string SeasonLabel => _picks?.SeasonLabel ?? string.Empty;

    /// <summary>
    /// The next quarter-hour plus <see cref="CheckOffset"/>. Anchored to the wall clock rather
    /// than to "now + 15 minutes" so every open tab is in step with the workflow and with the
    /// other tabs, whenever each of them was opened.
    /// </summary>
    private static DateTimeOffset NextCheckAfter(DateTimeOffset now)
    {
        var boundary = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Offset);

        while (boundary + CheckOffset <= now)
        {
            boundary += CheckInterval;
        }

        return boundary + CheckOffset;
    }

    protected override async Task OnInitializedAsync()
    {
        try
        {
            _picks = SweepstakeJson.ReadPicks(await Http.GetStringAsync("data/picks.json", _shutdown.Token));
        }
        catch (Exception ex)
        {
            // Without picks there is no board at all, so this one is worth surfacing.
            _loadError = ex.Message;
            return;
        }

        // A missing stats.json is survivable: render everyone on zero and say so.
        var stats = await TryLoadStatsAsync();
        _haveRealStats = stats is not null;
        Rebuild(stats ?? EmptyStats(_picks.Season), highlightChanges: false);
        _loaded = true;

        _ = PollAsync();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            return;
        }

        try
        {
            // No CancellationToken on these: the overload that takes one is easy to bind to by
            // accident, and the token then gets serialized as a JS argument and blows up.
            _visibility = await Js.InvokeAsync<IJSObjectReference>("import", "./js/visibility.js");
            _self = DotNetObjectReference.Create(this);
            _tabVisible = await _visibility.InvokeAsync<bool>("subscribe", _self);
        }
        catch (Exception ex) when (ex is JSException or TaskCanceledException or OperationCanceledException)
        {
            // Without the hook the timer simply polls regardless. Not worth failing over.
            Console.Error.WriteLine($"visibility hook unavailable: {ex.Message}");
        }
    }

    /// <summary>Called from JS when the tab is shown or hidden.</summary>
    [JSInvokable]
    public async Task OnVisibilityChanged(bool visible)
    {
        _tabVisible = visible;

        // Coming back to a tab that has been parked for a while: catch up straight away, and
        // resync the countdown, which will have been sitting at 0:00 while the tab was hidden.
        if (visible)
        {
            await CheckAsync();
        }
    }

    private async Task PollAsync()
    {
        // A tick a second, so the fetch fires on the second the footer counted down to rather
        // than up to a quarter of an hour after it.
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        try
        {
            while (await timer.WaitForNextTickAsync(_shutdown.Token))
            {
                if (DateTimeOffset.UtcNow < _nextCheckUtc)
                {
                    continue;
                }

                // No point polling a background tab; OnVisibilityChanged catches it up.
                if (_tabVisible)
                {
                    await CheckAsync();
                }
                else
                {
                    _nextCheckUtc = NextCheckAfter(DateTimeOffset.UtcNow);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Page is going away.
        }
    }

    /// <summary>One look for new numbers, with the footer saying so while it happens.</summary>
    private async Task CheckAsync()
    {
        _checking = true;
        await InvokeAsync(StateHasChanged);

        await RefreshAsync();

        // Recomputed from the clock afterwards, not from the instant this check was due, so a
        // slow fetch cannot drift the whole schedule away from the workflow's.
        _checking = false;
        _nextCheckUtc = NextCheckAfter(DateTimeOffset.UtcNow);
        await InvokeAsync(StateHasChanged);
    }

    private async Task RefreshAsync()
    {
        var stats = await TryLoadStatsAsync();

        // A failed refresh keeps the last good board on screen and tries again next tick.
        if (stats is null)
        {
            return;
        }

        // Nothing new upstream: do not recompute and do not re-render.
        if (_stats is not null && stats.GeneratedUtc == _stats.GeneratedUtc)
        {
            return;
        }

        _haveRealStats = true;
        Rebuild(stats, highlightChanges: true);
        await InvokeAsync(StateHasChanged);
        _ = ClearHighlightsLaterAsync();
    }

    private async Task<StatsFile?> TryLoadStatsAsync()
    {
        try
        {
            // GitHub Pages caches hard; without the query the browser serves the same bytes back.
            var url = $"data/stats.json?t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            return SweepstakeJson.ReadStats(await Http.GetStringAsync(url, _shutdown.Token));
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"stats refresh failed, keeping the last good board: {ex.Message}");
            return null;
        }
    }

    private void Rebuild(StatsFile stats, bool highlightChanges)
    {
        if (_picks is null)
        {
            return;
        }

        var goals = LeaderboardBuilder.Build(_picks, stats, Board.Goals);
        var assists = LeaderboardBuilder.Build(_picks, stats, Board.Assists);

        if (highlightChanges)
        {
            Diff(_goals, goals, _changedGoals, _goalRankMoves);
            Diff(_assists, assists, _changedAssists, _assistRankMoves);
        }

        _goals = goals;
        _assists = assists;
        _stats = stats;
    }

    /// <summary>Records which values moved and which entrants changed rank, for the animations.</summary>
    private static void Diff(
        IReadOnlyList<ScoredEntrant> before,
        IReadOnlyList<ScoredEntrant> after,
        HashSet<string> changedValues,
        Dictionary<string, int> rankMoves)
    {
        changedValues.Clear();
        rankMoves.Clear();

        if (before.Count == 0)
        {
            return;
        }

        var previous = before.ToDictionary(e => e.Name, StringComparer.Ordinal);

        foreach (var entrant in after)
        {
            if (!previous.TryGetValue(entrant.Name, out var was))
            {
                continue;
            }

            if (was.Rank != entrant.Rank)
            {
                rankMoves[entrant.Name] = entrant.Rank - was.Rank;
            }

            var wasByPick = was.Picks.ToDictionary(p => p.EspnId, p => p.Value, StringComparer.Ordinal);

            foreach (var pick in entrant.Picks)
            {
                if (wasByPick.TryGetValue(pick.EspnId, out var oldValue) && oldValue != pick.Value)
                {
                    changedValues.Add(BoardPanel.ValueKey(entrant.Name, pick.EspnId));
                }
            }
        }
    }

    private async Task ClearHighlightsLaterAsync()
    {
        var generation = ++_highlightGeneration;

        try
        {
            await Task.Delay(HighlightDuration, _shutdown.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // A newer refresh has already re-armed the highlights; leave them alone.
        if (generation != _highlightGeneration)
        {
            return;
        }

        _changedGoals.Clear();
        _changedAssists.Clear();
        _goalRankMoves.Clear();
        _assistRankMoves.Clear();
        await InvokeAsync(StateHasChanged);
    }

    private static StatsFile EmptyStats(string season) =>
        new(DateTimeOffset.UnixEpoch, season, "none", new Dictionary<string, PlayerStat>(StringComparer.Ordinal));

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();

        if (_visibility is not null)
        {
            try
            {
                await _visibility.InvokeVoidAsync("unsubscribe");
                await _visibility.DisposeAsync();
            }
            catch (Exception ex) when (ex is JSDisconnectedException or JSException or OperationCanceledException)
            {
                // The page is unloading; the handler goes with it.
            }
        }

        _self?.Dispose();
        _shutdown.Dispose();
    }
}
