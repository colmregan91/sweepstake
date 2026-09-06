# CLAUDE.md — PL Sweepstake Leaderboard

Project context for Claude Code. Read this before making changes.

---

## What this is

A season-long Premier League sweepstake leaderboard for a group of 17 friends. Each entrant
picked **3 goalscorers** and **3 assisters** before the 2026/27 season. Most goals wins the
left-hand board; most assists wins the right-hand board.

The app is a **read-only scoreboard**. There is no login, no user input, no writes, no
database. Picks are fixed for the season and live in `data/picks.json`.

---

## Two constraints that drive the whole architecture

Understand these before proposing changes, because most "obvious" designs violate one of them.

### 1. GitHub Pages is static-only

GitHub Pages serves files. It runs no server code — no ASP.NET Core host, no API controllers,
no server-side rendering, no middleware.

Consequences that are non-negotiable:

- **Blazor WebAssembly (standalone), never Blazor Server.** Blazor Server needs a live
  SignalR connection to a .NET process on a server. There is no server. If you find yourself
  reaching for `builder.Services.AddServerSideBlazor()` or a `Program.cs` with
  `app.MapRazorComponents<App>()`, you have taken a wrong turn.
- No `HttpContext`, no server-side secrets, no runtime configuration.
- Everything the app needs at runtime must already be a file in `wwwroot`.

### 2. The browser never calls ESPN

Stats are fetched **at build time** by a console tool, written to a static JSON file, and
committed. The Blazor app reads that JSON from its own origin.

Reasons, in order of importance:

- **CORS.** ESPN's API is undocumented and offers no cross-origin guarantee. A browser
  request from `*.github.io` may be blocked by the browser, and we cannot fix that from our
  side. A same-origin fetch of our own `data/stats.json` can never be blocked.
- **Cost per visitor.** Client-side fetching means every page load hits ESPN. Build-time
  fetching means ESPN sees a handful of requests a day regardless of traffic.
- **Resilience.** If ESPN is down or changes shape, the last good `stats.json` is still in
  the repo and the site still renders. Failure is deferred to CI, where it is visible and
  harmless, instead of hitting users.
- **Speed.** No network round-trip on load.

**Do not add a runtime `HttpClient` call to any third-party host.** The only `HttpClient`
usage in the web app is fetching our own `data/stats.json` relative to `<base href>`.

---

## Data source: ESPN core API

Undocumented, free, no API key, no signup. All endpoints below were verified working on
2026-08-22. Season key is the **starting year**, so `2026` = the 2026/27 season.
`types/0` means season aggregate.

```
# 20 PL teams (ids only)
https://sports.core.api.espn.com/v2/sports/soccer/leagues/eng.1/seasons/2026/teams?limit=50

# squad WITH names + athlete ids in one call (site API, not core API)
https://site.api.espn.com/apis/site/v2/sports/soccer/eng.1/teams/{teamId}/roster

# season leaders — LAGS A MATCHDAY BY HOURS. Cross-check only, never the source. See below.
https://sports.core.api.espn.com/v2/sports/soccer/leagues/eng.1/seasons/2026/types/0/leaders?limit=1000

# per-player season totals — lags identically. Not usable as a live source.
https://sports.core.api.espn.com/v2/sports/soccer/leagues/eng.1/seasons/2026/types/0/athletes/{athleteId}/statistics/0

# THE LIVE PATH — one athlete's fixtures, each with a per-match statistics link
https://sports.core.api.espn.com/v2/sports/soccer/leagues/eng.1/seasons/2026/athletes/{athleteId}/eventlog

# per-fixture stats for one athlete (the statistics $ref above resolves to this)
# offensive.totalGoals and offensive.goalAssists are the two numbers we want
https://sports.core.api.espn.com/v2/sports/soccer/leagues/eng.1/events/{eventId}/competitions/{eventId}/competitors/{teamId}/roster/{athleteId}/statistics/0
```

### The season endpoints lag. Do not go back to them.

Measured on 2026-08-22 at 20:36 UTC. Six fixtures had finished that day (14 goals). Every
season-level endpoint still reported only the previous day's fixture:

| Endpoint | Reported | Live? |
|---|---|---|
| `types/0/leaders` | 3 goals | no |
| `types/1/leaders` | 3 goals | no |
| per-athlete `statistics/0` | returned *no offensive stats at all* for a player who scored that day | no |
| **`eventlog` → per-fixture statistics** | correct within minutes of full time | **yes** |

So season totals are **summed from fixtures**, not read from the rollup. A player's total is
the sum of `offensive.totalGoals` across the fixtures their event log marks `played`.

Consequences to keep in mind:

- ESPN only creates an event log once a player has **featured**. A player with no log has no
  fixtures, so scores 0. That is correct, not a gap — verified against a player who was in the
  squad but did not play.
- The rollup is still fetched once per sweep as an **advisory cross-check**. It lagging behind
  us is expected. It being *ahead* of us would mean our summing dropped a fixture, which is the
  bug worth catching, so that case is logged loudly. It never fails the run.
- Opta reassigns goals and assists retrospectively. A cached fixture is therefore re-read for
  seven days before being treated as settled — see `data/match-stats.json` below.

Notes:

- The **core** API is `$ref`-heavy: list endpoints return links, not objects. The **site**
  API returns fat objects. Use the site API for rosters (1 call per club) rather than
  dereferencing 30 athlete `$ref`s per club (600 calls).
- The `leaders` payload has a `categories` array of **12** entries. Four of them are goal- or
  assist-related and two pairs share a `displayName`:

  | `name` | `displayName` | use |
  |---|---|---|
  | `goals` | Goals | **read this** |
  | `goalsLeaders` | Goals | cross-check only |
  | `assists` | Assists | **read this** |
  | `assistsLeaders` | Assists | cross-check only |

  Select by the exact `name` field. **Never match on `displayName`** — it is ambiguous, and
  which of the pair you get would depend on array order, which is not guaranteed. The
  `*Leaders` variants carry extra context (matches played) and are useful as a sanity check:
  if `goals` and `goalsLeaders` disagree for the same athlete, something has changed upstream
  and the fetch should warn.
- A player absent from the leaders list has **0** — that is not an error, just nobody who has
  scored yet.
- CORS: verified 2026-08-22 that this endpoint returns permissive CORS headers, so a browser
  on another origin *can* call it directly. We still fetch at build time — see the constraints
  section above for why. Do not treat the CORS result as a reason to change the architecture.
- Team ids for 2026/27: 306, 331, 337, 349, 357, 359, 360, 361, 362, 363, 364, 366, 367,
  368, 370, 373, 382, 384, 388, 393. Do not hardcode these; fetch the teams list.

### Why ESPN and not something else

This was researched and settled. Do not silently swap the data source.

- ESPN's numbers match the **official Opta** Premier League figures exactly. Verified against
  completed 2025/26: it returned Haaland 27 goals and Bruno Fernandes 21 assists, which are
  the actual Golden Boot and Playmaker Award numbers.
- The **Fantasy Premier League API was rejected**, despite being free, unlimited and
  officially supported. FPL exposes exactly one assist field and it is Opta's *Fantasy* Goal
  Assist variant, which credits assists for winning penalties, for own goals, and for
  rebounds — inflating totals well above the official leaderboard. There is no strict-assist
  field in the FPL payload to select instead. FPL is fine for goals but wrong for assists,
  and using two sources for the two boards would be inconsistent.
- ESPN's tradeoff is that it is undocumented: no terms of use granting access, no SLA, no
  published rate limit, and it can change without notice. That risk is contained by fetching
  at build time and keeping the last good JSON committed.

---

## Repository layout

```
/
├─ CLAUDE.md
├─ BUILD_BRIEF.md               # the original build spec
├─ .github/workflows/
│  ├─ update-stats.yml          # scheduled: refresh stats.json, commit. Throttled -- see below
│  ├─ matchday.yml              # one long job that loops, for when there is football on
│  └─ deploy.yml                # build + publish to Pages
├─ data/
│  ├─ picks.json                # HAND-MAINTAINED. Player registry + the 17 entrants' picks,
│  │                            #   keyed by ESPN athlete id. Committed. Reviewed by humans.
│  └─ match-stats.json          # GENERATED. Per-fixture goals/assists cache. Committed, but
│                               #   never served to the browser. Delete it to force a rebuild.
├─ src/
│  ├─ Sweepstake.Core/          # models + leaderboard calculation. No I/O. Unit tested.
│  └─ Sweepstake.Web/           # Blazor WASM app
│     └─ wwwroot/data/stats.json   # GENERATED. Committed. What the app actually reads.
├─ tools/
│  └─ Sweepstake.StatsFetcher/  # console app: ESPN -> stats.json
└─ tests/
   └─ Sweepstake.Core.Tests/
```

### Why `Sweepstake.Core` is a separate project

The leaderboard maths (join picks to stats, sum three players, sort, rank, handle ties) is
the only part with real logic, and it is pure: data in, data out, no network, no UI. Keeping
it in its own library means it can be unit tested without spinning up a browser or touching
ESPN. Both the web app and the fetcher reference it, so the models cannot drift apart.

---

## Data contracts

`data/picks.json` — hand-maintained, two sections:

- `players` — a registry of the 38 distinct picked players, **keyed by ESPN athlete id**,
  each with a display `name` and `club` (and `sheetName` where the original spreadsheet used
  a different name, e.g. `301894` is "Igor Thiago" but the sheet said "Thiago").
- `entrants` — each entrant's three goalscorers and three assisters, referenced purely by
  `espnId` plus display-only `odds`.

**There is no string matching anywhere at runtime.** A pick is an id; a stat is keyed by the
same id; the join is integer-to-integer. Names exist only to be printed on screen. This is
deliberate — see the rule below.

Ids were resolved from live ESPN season-2026 rosters on 2026-08-22, each confirmed against
the athlete endpoint, and reviewed by hand. `club` is display-only and must never be used for
matching.

`wwwroot/data/stats.json` — generated. Shape:

```json
{
  "generatedUtc": "2026-08-22T18:00:00Z",
  "season": "2026",
  "source": "espn-core-api",
  "players": {
    "<espnAthleteId>": { "name": "Alexander Isak", "goals": 3, "assists": 1 }
  }
}
```

Keep `generatedUtc`. The app uses it to tell a new `stats.json` from the one it already holds,
and the fetcher rewrites it every run.

It is deliberately **not** shown in the UI. `update-stats.yml` commits only when a player's
total actually moves, so the timestamp stands still through every check that finds nothing —
which is most of them, and all of them during an international break. A board checked three
minutes ago therefore reads as hours stale, and "hours stale" is indistinguishable on screen
from a fetcher that has been broken since Tuesday. The timestamp could not tell those two
apart, so it was not the honest option it looks like.

The footer counts down to the page's next check instead. That is a claim the page can make
truthfully on its own, without needing to know anything the server did not tell it.

`data/match-stats.json` — generated, committed, never served. What each fixture contributed,
per player:

```json
{
  "season": "2026",
  "updatedUtc": "2026-08-22T21:39:26Z",
  "lastSweepUtc": "2026-08-22T21:39:26Z",
  "fixtures": {
    "<espnEventId>": {
      "firstSeenUtc": "2026-08-22T21:39:26Z",
      "lastReadUtc": "2026-08-22T21:39:26Z",
      "players": { "<espnAthleteId>": { "goals": 1, "assists": 0 } }
    }
  }
}
```

This file is what keeps the request count flat instead of growing with the season, rather
than proportional to how often we look. Three timers drive it:

- **hot** (< 6h since first seen) — re-read every run, because the match may still be in play
- **unsettled** (6h to 7 days) — re-read hourly, purely to absorb Opta corrections
- **settled** (> 7 days) — never read again unless `fetch --rebuild`

Whole-sweep skipping sits on top: if nothing is hot and we swept within the hour, the run
makes **no requests at all** and rewrites `stats.json` with the same numbers.

### Why there are two workflows, and why the frequent one is a loop

`update-stats.yml` asks for a run every 15 minutes. **GitHub does not give us one.** Measured
on 2026-08-24, that schedule fired 19 times between 04:08Z and 19:46Z — one run every 49
minutes on average, about one trigger in three. Scheduled workflows are best-effort and are
throttled hardest at the top of the hour, which is exactly when matches kick off. Every run
succeeded; there was nothing to fix. The scheduler simply will not run a `*/15` cron at
`*/15`.

So frequency does not come from the scheduler. `matchday.yml` takes **one** trigger and loops
inside a single job, fetching every 5 minutes for two hours. The cron only has to land once
per window, and starting 40 minutes late costs far less than dropping three refreshes in four.
`update-stats.yml` is unchanged and still carries quiet days.

They share a concurrency group deliberately — both commit the same two files to the same
branch, and GitHub keeps at most one run pending per group, so scheduled runs cannot pile up
behind a long watch.

**The cost is requests.** A watch window is ~24 passes at ~45 requests each, so a match day is
roughly 5–6k requests against ESPN rather than the ~60 it was, and a quiet day ~400 once
sweep-skipping takes hold. ESPN publishes no rate limit, so this is a judgement call rather
than a measured safe number. The levers, in the order worth reaching for: widen `interval`,
narrow the cron to real fixture windows instead of every day, shorten `minutes`.

`matchday.yml` pushes with `STATS_PUSH_TOKEN` when that secret exists, because a push made
with `GITHUB_TOKEN` does not trigger `deploy.yml` — without the PAT its commits reach the site
only when the whole watch ends, which is hours too late to be worth having.

---

## Rules

- **Derive, never hardcode.** Per-player goals/assists come from `stats.json`. Entrant totals
  are summed at runtime. The odds "Totals" column is the sum of the three odds numerators
  (verified: 8+40+80 = 128). No number in the UI should be a literal copied from the
  spreadsheet.
- **Never match players by name.** Ids only. Name matching is fragile in football in ways
  that are not obvious: accents (Ødegaard, Gyökeres, Šeško, Muñoz, Guimarães, Groß, Jérémy),
  providers disagreeing on the form of a name (ESPN calls `301894` "Igor Thiago"; the
  spreadsheet said "Thiago"), several active players sharing a name (there are two Igor
  Jesuses and ten Bruno Fernandeses in ESPN's database), and mid-season transfers. A fuzzy
  matcher loose enough to catch all of those is loose enough to eventually match the wrong
  player — and a wrong player produces plausible numbers that nobody notices. Ids do not have
  this failure mode.
- **A missing player is 0, not a crash.** Injured, transferred abroad, or simply yet to score
  — all render as 0. But if an id in `picks.json` is absent from the `players` registry, or
  vice versa, that is a real bug: fail the build.
- Names are display-only and are UTF-8. Do not "fix" a name by stripping its accents.
- Never commit a `stats.json` that failed to fetch. CI should abort and leave the last good
  file in place.
- No analytics, no trackers, no external fonts or CDNs. Self-contained.

---

## Commands

```bash
# verify every id in picks.json still resolves to the expected player at ESPN
# (maintenance only -- run after a transfer window, not in the deploy path)
dotnet run --project tools/Sweepstake.StatsFetcher -- verify

# refresh stats.json from live per-fixture data (incremental; run by CI every 15 min)
dotnet run --project tools/Sweepstake.StatsFetcher -- fetch

# same, but re-read every fixture including settled ones. Run nightly by CI, or by hand if
# you suspect the cache has drifted. Deleting data/match-stats.json has the same effect.
dotnet run --project tools/Sweepstake.StatsFetcher -- fetch --rebuild

dotnet test
dotnet run --project src/Sweepstake.Web
dotnet publish src/Sweepstake.Web -c Release
```

---

## GitHub Pages deployment gotchas

These are the four things that break Blazor WASM on Pages. Get them right once.

1. **`.nojekyll`** must exist in the published output root. GitHub Pages runs Jekyll by
   default, and Jekyll ignores directories starting with an underscore — which silently
   deletes Blazor's entire `_framework` folder. The symptom is a blank white page with 404s
   on `blazor.webassembly.js`. This is the single most common failure.
2. **`<base href>`** in `index.html` must be `/<repo-name>/` for a project site, not `/`.
   If the site is served at `user.github.io/sweepstake/`, the base href is
   `/sweepstake/`. Rewrite it during the publish step rather than committing it, so
   local `dotnet run` still works with `/`.
3. **`404.html`** must be a copy of `index.html`. Pages has no SPA rewrite rule, so any deep
   link 404s without it.
4. Publish output lives at `bin/Release/net10.0/publish/wwwroot` — that subdirectory is what
   gets uploaded, not the `publish` folder itself.

---

## Style

Target framework `net10.0`. Nullable enabled, implicit usings enabled, warnings as errors in
CI. Records for DTOs. `System.Text.Json` with source generation (trimming-friendly, and
Blazor WASM trims aggressively). No third-party UI framework — plain CSS. Keep the component
tree shallow; this is a table, not an application.
