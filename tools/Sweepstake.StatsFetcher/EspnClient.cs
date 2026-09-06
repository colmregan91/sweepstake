using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Sweepstake.StatsFetcher;

/// <summary>
/// A deliberately polite client for an undocumented API we have no agreement with: a real
/// User-Agent, a modest concurrency cap, and backoff on 429 and 5xx.
/// </summary>
internal sealed class EspnClient : IDisposable
{
    private const string UserAgent =
        "sweepstake/1.0 (+https://github.com/colmregan91/sweepstake) build-time stats fetcher";

    private const int MaxConcurrentRequests = 4;

    private static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
    ];

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(MaxConcurrentRequests, MaxConcurrentRequests);
    private readonly TextWriter _log;
    private readonly string _season;
    private int _requestCount;

    public EspnClient(string season, TextWriter log)
    {
        _season = season;
        _log = log;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public int RequestCount => _requestCount;

    private string SeasonRoot =>
        $"https://sports.core.api.espn.com/v2/sports/soccer/leagues/eng.1/seasons/{_season}";

    public string AthleteUrl(string athleteId) => $"{SeasonRoot}/athletes/{athleteId}";

    public string TeamUrl(string teamId) => $"{SeasonRoot}/teams/{teamId}";

    public string LeadersUrl() => $"{SeasonRoot}/types/0/leaders?limit=1000";

    /// <summary>
    /// The event log paginates at 25 by default, which silently truncates a full 38-fixture
    /// season and undercounts every total. Always ask for the whole thing.
    /// </summary>
    public const int EventLogLimit = 100;

    public string AthleteEventLogUrl(string athleteId) =>
        $"{SeasonRoot}/athletes/{athleteId}/eventlog?limit={EventLogLimit}";

    public string AthleteStatisticsUrl(string athleteId) =>
        $"{SeasonRoot}/types/0/athletes/{athleteId}/statistics/0";

    /// <summary>Fetches and deserializes. Returns null on 404; throws on anything else.</summary>
    public async Task<T?> GetOrNullAsync<T>(string url, JsonTypeInfo<T> typeInfo, CancellationToken ct)
        where T : class
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var response = await SendWithRetryAsync(url, ct);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"ESPN returned {(int)response.StatusCode} {response.ReasonPhrase} for {url}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await JsonSerializer.DeserializeAsync(stream, typeInfo, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Fetches and deserializes. Throws if the resource is missing or empty.</summary>
    public async Task<T> GetAsync<T>(string url, JsonTypeInfo<T> typeInfo, CancellationToken ct)
        where T : class =>
        await GetOrNullAsync(url, typeInfo, ct)
        ?? throw new HttpRequestException($"ESPN returned no usable body for {url}");

    private async Task<HttpResponseMessage> SendWithRetryAsync(string url, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            Interlocked.Increment(ref _requestCount);

            HttpResponseMessage? response = null;
            TimeSpan wait;

            try
            {
                response = await _http.GetAsync(url, ct);

                // 404 is a real answer here (the id is gone), not a transient failure.
                if (response.StatusCode != HttpStatusCode.TooManyRequests && (int)response.StatusCode < 500)
                {
                    return response;
                }

                if (attempt >= Backoff.Length)
                {
                    return response;
                }

                wait = RetryAfter(response) ?? Backoff[attempt];
                _log.WriteLine($"  ! HTTP {(int)response.StatusCode} for {url} - retrying in {wait.TotalSeconds:0.#}s");
                response.Dispose();
            }
            catch (Exception ex) when (attempt < Backoff.Length && IsTransient(ex, ct))
            {
                response?.Dispose();
                wait = Backoff[attempt];
                _log.WriteLine($"  ! {ex.GetType().Name} for {url} - retrying in {wait.TotalSeconds:0.#}s");
            }

            await Task.Delay(wait, ct);
        }
    }

    private static bool IsTransient(Exception ex, CancellationToken ct) => ex switch
    {
        OperationCanceledException when ct.IsCancellationRequested => false,
        // A per-request timeout surfaces as TaskCanceledException with the token unset.
        OperationCanceledException or HttpRequestException or IOException => true,
        _ => false,
    };

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
        {
            return delta;
        }

        if (header?.Date is { } date)
        {
            var until = date - DateTimeOffset.UtcNow;
            return until > TimeSpan.Zero ? until : TimeSpan.Zero;
        }

        return null;
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}
