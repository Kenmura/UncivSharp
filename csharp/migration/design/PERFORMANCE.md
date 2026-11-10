# Turn-time performance: harness, baseline and hotspots (T261)

<!-- toc -->
## Contents

- [The harness](#the-harness)
- [Baseline](#baseline)
  - [Per-turn growth](#per-turn-growth)
  - [Medium scenario, phases (ms per turn, run 3)](#medium-scenario-phases-ms-per-turn-run-3)
  - [Per civ (ms per turn, last quarter, start + automate + end)](#per-civ-ms-per-turn-last-quarter-start--automate--end)
  - [Checkpoint hash, small and medium](#checkpoint-hash-small-and-medium)
  - [Host-side view building (medium, turns 198-200, 3 recipients, 54 commands)](#host-side-view-building-medium-turns-198-200-3-recipients-54-commands)
- [Ranked hotspots](#ranked-hotspots)
- [T351: call-level breakdown of AI unit moves](#t351-call-level-breakdown-of-ai-unit-moves)
  - [Follow-ups filed from this profile](#follow-ups-filed-from-this-profile)
- [Result of T262](#result-of-t262)
- [Targets recorded for T262 and T263](#targets-recorded-for-t262-and-t263)
- [Parallelism](#parallelism)
  - [Method and prototype](#method-and-prototype)
  - [Candidates](#candidates)
  - [Ordered list of safe candidates and the risks](#ordered-list-of-safe-candidates-and-the-risks)
  - [Cache-write audit of host view building (T296)](#cache-write-audit-of-host-view-building-t296)
  - [Result of T297 (the go, implemented)](#result-of-t297-the-go-implemented)
- [Result of T342 (incremental host views, implemented)](#result-of-t342-incremental-host-views-implemented)
- [Follow-ups](#follow-ups)
<!-- /toc -->

GitHub issue #2: turns take a while to process. This page is the measurement that T262 (optimise) and T263 (parallelism spike) start from.
Nothing here is an optimisation; the only engine change is the `TurnProbe` measurement seam.

## The harness

`dotnet run -c Release --project Unciv.Cli -- bench [--scenario small|medium|all] [--turns N] [--repeat N] [--json FILE|-] [--host-turns K] [--recipients R] [--no-probe]`
(`bench --help` prints the options). It is a CLI command, not a test: plain `dotnet test` never runs it. It generates two fixed-seed games itself (no save
files) on the generated Brave New World ruleset, plays them through the command layer the way the BNW soak does (scripted human, AI civs through `RunAiTurn`,
a state-hash checkpoint after every human turn start, nothing else scripted) and reports wall time per turn split by phase.

| Scenario | Civs | City states | Map | Seeds (game / map) | Turns |
|---|---|---|---|---|---|
| small | 4 | 2 | Small, Small Continents | 11 / 5 | 50 |
| medium | 8 | 8 | Medium, Continent and Islands | 23 / 9 | 200 |

- **Probe, not a clock in the engine.** `Unciv.Utils.TurnProbe` lets the engine announce nested, named phases (`TurnProbe.Measure("Start.Cities")`) to an optional
  observer; it reads no clock, no state, and costs one thread-static read when nobody listens. The bench is the observer and does all timing with `Stopwatch`
  (one narrowly justified suppression). Phases are in `TurnManager` (start, automate, end of a civ, with the civ id as subject), `GameInfo.RefreshTransientCaches`
  and `UpdateCivilizationState`, the AI (`AI.*`), the command layer (`Command.Apply`, subject = command type), the checkpoint hash (`Hash.*`) and the host view
  builders (`View.*`). Add a `Measure` line where a drill-down is needed; the bench lists every phase it sees.
- **Repeat and spread.** `--repeat N` (1-20) plays every scenario N times and reports the **median** of the runs: the report detail is the median run (the run whose
  late median is the median), while the scenario's `lateMedianWallMs` and `totalSeconds` are the medians across runs and carry `...Min`, `...Max` and
  `...SpreadPercent` ((max - min) as a percentage of the median). The JSON `runs` array lists each run's late median and total. The spread is the main limit of the
  numbers on a shared machine (the T262 comparison is a median of runs for this reason). `--golden` compares the median run; every repeat must carry the same
  per-turn state hashes, and a difference fails instead of being averaged away.
- **Report.** Per turn: wall ms, state hash, city and unit counts, GC pause and gen-2 count, inclusive ms of the main phases. Per scenario: the top 20 phases by inclusive
  time (with exclusive time, calls, mean per call) for the whole run, the first quarter and the last quarter, the time per civ (start, automate, end), per command type, and
  the `lateMedianWallMs` (median of the last fifth of the turns) that T262's target uses. Inclusive time of a parent contains its children; exclusive does not.
- **Host cost.** For the last K turns (medium: 3) the bench acts as R recipients (default 3, the first living majors) of a `GameHost`: around every command it does what
  `GameHost.CaptureViewMarks` and `BroadcastDelta` do (`ViewDeltaBuilder.Capture` before; `BuildWithDiff` and `ProtocolCodec.Encode` after), without engine events. That time
  is taken out of the engine numbers (it is booked on a separate recorder and subtracted from the clock), so the engine columns are those of a game without recipients.
- **Proof of unchanged state.** `--no-probe` plays without the probe and without host measurement. `BenchTests` (Unciv.App.Tests, every build) compares the per-turn
  state hashes of a probed run with host measurement to a run without the probe: they must be equal. The JSON lists the hash of every turn (the golden list T262 freezes).
- **Tools.** `dotnet-trace` is installed in `dotnet-dev` as a global tool at `~/.dotnet/tools/dotnet-trace` (not on `PATH`): the CPU-sampling profile is
  `--profile dotnet-sampled-thread-time --format Speedscope` (~100 Hz stacks; `cpu-sampling` belongs to `collect-linux`). It supplied the call-level detail
  below the Stopwatch probes for T351 (see [T351](#t351-call-level-breakdown-of-ai-unit-moves)); `dotnet-counters` is still unused.

## Baseline

Machine: Intel Core Ultra 7 265 (20 logical cores), 30 GB, Fedora 44 container, .NET 10.0.9, Release build, workstation (non-server) GC, one game thread.
The machine was shared with other agents' builds and tests, so **timings vary by about +-30% between runs** (late medians of three consecutive medium runs:
933, 823 and 549 ms; the first used fewer probe points). Compare before/after in the same session, several runs each, never against these absolute numbers
alone. The shares between phases were stable across the runs.

### Per-turn growth

| Scenario | Median turn, first quarter | Median turn, last quarter | Late median (last fifth) | Total |
|---|---|---|---|---|
| small (turns 1-50) | 77 ms | 103 ms | 107 ms | 4.7 s |
| medium (turns 1-200) | 179 ms | 556 ms | 549 ms (run 2: 823) | 72 s (run 2: 99 s) |

The medium game ends with 17 civs (6 major AI, 8 live city states in the last quarter), 30 cities and about 230 units. The scripted human dies at turn 167, after which
the host plays its turns and the checkpoint hash stops: the per-turn hash numbers below are for the turns before that, the final-state hash is measured separately.
Per-turn spikes are large: the slowest medium turns (190-196) take 1.2-2.7 s while their neighbours take 0.6 s; the spike is in `RefreshTransientCaches` (up to 1.7 s
in one turn) and coincides with a gen-2 GC; the cause was not isolated (follow-up).

### Medium scenario, phases (ms per turn, run 3)

| Phase | First quarter (turns 1-50) | Last quarter (turns 151-200) | Calls per turn (last) |
|---|---|---|---|
| Command.Apply (all engine work of the commands) | 181 | 652 | 20 |
| RefreshTransientCaches | 106 | 380 | 19 |
| - UpdateCivilizationState (inside it) | 89 | 351 | 19 |
| - - CityStatsUpdate (`city.CityStats.Update`) | 33 | 141 | 322 |
| - - TileStatsAndHappiness | 12 | 88 | 322 |
| - - UnitVisibleTiles | 13 | 40 | 322 |
| - - UpdateOurTiles | 17 | 39 | 322 |
| - - UpdateSightAndResources | 12 | 23 | 322 |
| - - CitiesConnectedToCapital | n/a | 21 | 322 |
| TurnManager.AutomateTurn (AI) | 41 | 186 | 16 |
| - AI.AutomateUnits | 29 | 156 | 15 |
| - - AI.AutomateUnitMoves (per unit, 223 calls) | 21 | 142 | 223 |
| - - AI.Homeland (queue incl. unit moves it drives) | 18 | 70 | 15 |
| TurnManager.StartTurn | 20 | 60 | 17 |
| - Start.Cities | 13 | 47 | 17 |
| TurnManager.EndTurn | 8 | 23 | 17 |
| - End.Cities | 3 | 10 | 16 |
| AI.Tactical / AI.UseGold | 3.5 / 3.9 | 7 / 6 | 15 |
| Checkpoint hash (`Hash.FullHash`, human alive) | 5 | n/a (human dead) | 1 |

`AI.Homeland` and `AI.AutomateUnitMoves` overlap (the homeland queue calls the same unit moves). The command-layer overhead outside the phases is negligible
(`Command.Apply` self time 1.4 ms per turn). Per command type (whole run): `run-ai-turn` 3199 calls, 20 ms mean; `begin-turn` 10 ms; `end-turn` 9 ms; everything the scripted
human does (unit actions) is below 1 ms per call.

### Per civ (ms per turn, last quarter, start + automate + end)

Majors cost 30-40 ms each (Russia 40, China 39, Greece 35, England 32, Rome 32, Egypt 30); city states 3-11 ms (Antwerp 11, France 10, Quebec City 8, ...);
Barbarians 0.2; the dead human 0.7. Over all civs the split of those phases is about 69% AI (`AutomateTurn` 186 ms), 22% start (60 ms), 9% end (23 ms). These sum to about 270 ms of the 650 ms of engine time:
**the other ~380 ms per turn is `RefreshTransientCaches`, which is charged to no civ** (it runs after every civ step).

### Checkpoint hash, small and medium

`ReplayVerifier.FullHash` serialises the whole game to JSON and FNV-hashes the text. With the human alive it costs 5 ms per turn on the medium game at turns 1-50 (4 ms
serialise, rest digest) and 2.4 ms on the small one; for the medium game's final state (turn 200, 30 cities, 17 civs) serialise is 9.3 ms and digest 1.9 ms. It is **one
checkpoint per human turn start, not per civ**, so it is under 2% of a late turn. The soak's 3 s per turn (B026) therefore does not come from the hash: it ran a Debug
build and, per 25 turns, a save/load round trip and a full replay, and scripted BNW actions through the dev console that this bench does not play.

### Host-side view building (medium, turns 198-200, 3 recipients, 54 commands)

| Step (per recipient, per command) | ms |
|---|---|
| `ViewDeltaBuilder.Capture` (before) | 0.49 |
| `SnapshotBuilder.Build` | 2.31 |
| `SnapshotDiff.Compute` | 1.71 |
| delta events (`Build`, no engine events passed) | 0.74 |
| `ProtocolCodec.Encode` | 0.10 |
| **Total** | **~5.4** |

A late turn has about 20 server commands (every `RunAiTurn`, the `BeginTurn`, the human's commands) and every connected player is a recipient for each of them, so the host
adds roughly 5.4 ms x 20 x recipients per turn: **about 110 ms per turn with one connected player, about 430 ms with four, about 860 ms with eight** at this game size, the same order as
the whole engine turn. The cost grows with the map explored and the unit count (the snapshot is rebuilt in full for every command). Only the first 3 recipients and 3 turns were
measured; the numbers are per recipient and linear in the recipients.

## Ranked hotspots

Ranked by their share of a late medium turn (last quarter, engine time about 650 ms per turn on average; GC pauses are inside that time, not on top). The shares are shares of mean times: the last-quarter mean (652 ms) is above its median (556 ms) because of the 1.2-2.7 s spike turns:

1. **`RefreshTransientCaches` is called after every civ step (19 times per turn) and each call runs `UpdateCivilizationState` for every civ (17), so the per-civ work
   (city stats, tile stats, vision, our tiles, connection to capital) runs 322 times per turn instead of 17.** About 380 ms per turn, 58% of the engine time, and 55 to 60% in
   every quarter; it grows with the city count (cities x civs x steps). The T218 rule (every step ends canonical, so a snapshot between steps reloads to the live state) is why
   it runs, but a civ whose state did not change in a step does not need its caches rebuilt. Largest inner items: `CityStats.Update` 141 ms, `UpdateTileStats`+happiness 88 ms,
   unit vision 40 ms, our tiles 39 ms. Optimising must keep every per-turn hash equal.
2. **AI unit automation, `AI.AutomateUnitMoves`: 142 ms per turn** (223 unit moves, 0.64 ms each, inclusive of pathfinding; 22% of the turn). Needs call-level data below the
   probe (pathfinding, `GetDistanceToTiles`, danger evaluation); candidate for a per-turn cache or a cheaper path query.
3. **Garbage collection: 10.7 s of pause in 72 s of run 3 (15%) over 47 gen-2 collections (the worst turns show 0.2 s of pause each; the total includes gen-0/1 pauses).** The turn allocates heavily (the `.ToList()` copies, LINQ, per-call dictionaries in
   the refresh); the biggest turns coincide with gen-2 collections. Not engine logic: allocation reduction in hotspot 1, or GC settings (concurrent/server GC, `TieredPGO`) in the host.
4. **`Start.Cities` (`CityTurnManager.StartTurn`) 47 ms per turn** and `End.Cities` 10 ms: per-city stats recomputation, partly the same `CityStats.Update` as hotspot 1.
5. **`UCS.CitiesConnectedToCapital` 21 ms** (part of hotspot 1): road/harbour connection BFS rerun for every civ at every step.
6. **Host view building, outside the engine turn: about 5.4 ms per recipient per command** (above). Not in the end-turn time of a single-player game, but it doubles it in an 8-player
   host game; an incremental diff builder (T178's follow-up) is the fix.

Small: the same order (refresh 52%, AI 20%, start 18%), turn 103 ms at turn 50.

## T351: call-level breakdown of AI unit moves

The follow-up "call-level profiling inside `AI.AutomateUnitMoves` and the spike turns" was done with both tools: the bench's phase probe grew call-level points
and a GC column per phase, and `dotnet-trace` supplied a sampling profile (it *is* installed in `dotnet-dev` as a global tool; the 2026-10 note said otherwise
because it is not on `PATH`). Commands (Release, from `csharp/`):

```
dotnet Unciv.Cli/bin/Release/net10.0/Unciv.Cli.dll bench --scenario medium --turns 200 --json /tmp/out.json
~/.dotnet/tools/dotnet-trace collect --profile dotnet-sampled-thread-time --format Speedscope -o /tmp/medium-trace -- \
    dotnet Unciv.Cli/bin/Release/net10.0/Unciv.Cli.dll bench --scenario medium --turns 200 --no-probe
```

(`--profile cpu-sampling` is only valid for `dotnet-trace collect-linux`; `dotnet-sampled-thread-time` is the ~100 Hz .NET stack sampler for `collect`.)
The probe points are `Unit.*` (one per branch of `AutomateUnitMoves`, now split into `TryAutomateSpecialUnit`), `Path.MovementToTiles`, `Path.DistanceToTiles`,
`Path.ShortestPath`, `Path.CanReach`, `Path.HeadTowards`, `Path.MoveToTile`, `Threat.DangerousTiles`, `Threat.DistanceToClosestEnemy` and
`AI.UnitPriorityOrder`. The writer of the probes is the bench, so **the engine is still clock-free**; without an observer a scope costs one thread-static read.
Every report row gained `gc0`/`gc1`/`gc2` (process-wide collections that started inside the phase; nested phases count the same collection, so read it next to
the parent's numbers) and the per-turn list gained `gen0`/`gen1`/`gen2`. Two new report sections: `callProfile` (every `Unit.*`/`Path.*`/`Threat.*` point, whole
run and last quarter) and `spikeTurns` (the five slowest turns with their top phases and GC counts).

Run: medium, 200 turns, Release on the shared 20-core machine. Three runs of the same tree gave 65.2 s / 477.8 ms, 60.6 s / 449.2 ms and 44.5 s / 314.1 ms late median (load varied and was not recorded), so compare **shares**, not absolute times: in all three `AI.AutomateUnitMoves` took 25-26% of `Command.Apply`, `Unit.Civilian` 40% of it and `Path.*` 38%. The tables below use the middle run (449.2 ms late median; human dead at turn 167 as before, 30 cities, 204 units at turn 200).

| Phase (last quarter, per turn) | inclusive ms | self ms | calls | alloc MB | gen0/gen1/gen2 |
|---|---|---|---|---|---|
| Command.Apply | 418.9 | 0.7 | 20 | 322.0 | 21.8 / 8.4 / 0.20 |
| RefreshTransientCaches | 211.4 | 33.7 | 19 | 153.1 | 10.3 / 4.2 / 0.10 |
| Refresh.UpdateCivilizationState | 177.7 | 0.4 | 19 | 135.8 | 9.2 / 3.8 / 0.10 |
| TurnManager.AutomateTurn | 142.8 | 15.6 | 16 | 125.8 | 8.5 / 3.2 / 0.10 |
| AI.AutomateUnits | 119.9 | 2.3 | 15 | 112.4 | 7.2 / 2.7 / 0.10 |
| **AI.AutomateUnitMoves** | **107.1** | 0.4 | **223** | **104.3** | **6.6 / 2.6 / 0.10** |
| AI.Homeland (queue that calls the moves) | 48.7 | 0.6 | 15 | 46.1 | 3.0 / 1.0 / 0.00 |
| Path.MovementToTiles | 41.2 | 41.2 | 6641 | 49.6 | 3.5 / 1.4 / 0.08 |
| Path.ShortestPath | 31.3 | 9.0 | 369 | 35.5 | 2.3 / 1.0 / 0.02 |
| Unit.Civilian | 42.8 | 30.8 | 73 | 42.2 | 2.7 / 0.9 / 0.02 |
| Path.CanReach | 21.6 | 0.1 | 316 | 24.7 | 1.5 / 0.6 / 0.02 |
| Path.DistanceToTiles | 18.5 | 0.3 | 1150 | 23.8 | 1.7 / 0.5 / 0.04 |
| Unit.EnemyCity | 18.1 | 1.7 | 117 | 18.0 | 1.1 / 0.6 / 0.00 |
| Path.HeadTowards | 14.1 | 0.1 | 112 | 12.9 | 1.1 / 0.3 / 0.02 |
| Path.MoveToTile | 12.4 | 10.4 | 181 | 9.3 | 0.7 / 0.2 / 0.00 |
| Unit.Explore | 9.9 | 2.6 | 62 | 11.6 | 0.7 / 0.3 / 0.00 |
| Unit.CloseEnemy | 8.4 | 2.0 | 70 | 8.5 | 0.5 / 0.2 / 0.02 |
| Unit.Attack | 7.7 | 5.4 | 122 | 6.5 | 0.4 / 0.1 / 0.02 |
| Unit.Garrison | 5.5 | 0.3 | 98 | 5.6 | 0.5 / 0.1 / 0.00 |
| Unit.AccompanySettler | 4.1 | 0.6 | 151 | 4.5 | 0.2 / 0.1 / 0.00 |
| Unit.Prepare | 3.2 | 1.0 | 70 | 2.9 | 0.2 / 0.1 / 0.02 |
| Threat.DistanceToClosestEnemy | 3.0 | 3.0 | 628 | 2.9 | 0.1 / 0.0 / 0.00 |
| all other Unit.* / Threat.* / AI.UnitPriorityOrder | < 7 | | | | |

`Unit.Civilian` self time is `UnitAutomation.GetDangerousTiles` (three explored-distance-3 scans plus a `ToHashSet`, computed per civilian unit) before
`CivilianUnitAutomation` runs; `Unit.Attack` self time is the per-tile attack/damage evaluation. The full rows, including the sub-millisecond tail, are in the
JSON's `callProfile`.

The sampler tells the same story by call path (52,764 CPU samples, 85.1 s CPU in a 87.5 s run; shares of the `AutomateUnitMoves` subtree, 16.1 s = 18.4% of the
run):

| Frame (inclusive, sample share) | share of AutomateUnitMoves |
|---|---|
| `CivilianUnitAutomation.AutomateCivilianUnit` | **50.2%** (8.08 s) |
| - `WorkerAutomation.AutomateWorkerAction` | 31.8% (5.13 s) |
| - - `WorkerAutomation.FindTileToWork` → `GetImprovementPriority` → `ChooseImprovement` | 25% |
| `UnitMovement` subtree (pathfinding) | **48.8%** (7.86 s) |
| - `GetMovementToTilesAtPositionCore` | 27.4% (4.42 s) |
| - `GetShortestPathCore` (inside `CanReach`/`HeadTowards`) | 22.3% (3.59 s) |
| - `CanReach` / `HeadTowards` / `MoveToTile` | 16.5% / 13.1% / 7.7% |

Self time inside the same subtree (deepest engine frame): `GetMovementToTilesAtPositionCore` 17.9%, `ImprovementFunctions.GetImprovementBuildingProblems`
11.4% (worker `ChooseImprovement`), `GetShortestPathCore` 5.6%, `TileMap.GetViewableTiles`'s neighbour lambda 3.8% (`TargetHelper.GetAttackableEnemies`),
`TryAdvanceTowardsCloseEnemy` 3.7%, `TileStatFunctions.GetTileStatsBreakdown` 3.7% (worker `GetImprovementRanking`), `KotlinLazy<bool>.get_Value` 2.6%,
`Tile.Neighbors` 2.5%, `ThreatManager.GetDistanceToClosestEnemyUnitCore` 2.3%, `TryGarrisoningLandUnit` 2.2%.

**Ranked costs inside `AI.AutomateUnitMoves` (107 ms per late turn):**

1. **BFS pathfinding, about 41 ms per turn (38%) and half its allocation.** `Path.MovementToTiles` is 6641 calls per turn (5.8 per `Path.DistanceToTiles`,
   because `GetShortestPath` re-expands `GetMovementToTilesAtPosition` once per frontier tile) and allocates 49.6 MB per turn in `PathsToTilesWithinTurn`
   dictionaries and `ParentTileAndTotalMovement` objects. `GetMovementToTilesAtPositionCore` is the top self frame in the sampler (17.9% of the unit-move
   subtree). `GetShortestPath` (31 ms) and its `CanReach` callers (22 ms) are the same BFS under a different name.
2. **Civilian automation, mostly worker improvement choice: 42.8 ms per turn (40%), 50% in the sampler.** For each worker and each candidate tile,
   `ChooseImprovement`/`GetImprovementRanking` evaluates every improvement's yield (`TileStatFunctions.GetTileStatsBreakdown`, 1.41 s of the subtree) and
   `ImprovementFunctions.GetImprovementBuildingProblems` (1.84 s, the single largest non-pathfinding self frame) walks the city's buildings and tech
   prerequisites again. None of it is memoised per turn.
3. **Target and garrison evaluation: about 40 ms per turn combined** (`Unit.EnemyCity` 18.1, `Unit.CloseEnemy` 8.4, `Unit.Attack` 7.7, `Unit.Garrison` 5.5).
   The sampler shows 0.61 s in `TileMap.GetViewableTiles`'s neighbour lambda reached from `TargetHelper.GetAttackableEnemies`, 0.60 s self in
   `TryAdvanceTowardsCloseEnemy` (its 3-turn movement BFS) and 0.36 s in `TryGarrisoningLandUnit` (every city tested with `CanMoveTo`/`CanReach`).
4. **Allocation and GC: 104.8 MB per turn, 6.6 gen-0, 2.6 gen-1 and 0.1 gen-2 collections per turn** (against Command.Apply's 322 MB, 21.8/8.4/0.2).
   Pathfinding contributes half the allocation; `CannotPassThroughReason` also allocates a fresh `KotlinLazy<bool>` on every call (0.42 s of
   `KotlinLazy.get_Value` in the sampler).
5. **The spike turns are not unit-move spikes.** Slowest medium turns: 197 (704 ms), 198 (697), 1 (668, warm-up), 196 (587), 200 (558). In all of them
   `RefreshTransientCaches` (286-361 ms) and `UpdateCivilizationState` (235-310 ms) dominate; `AI.AutomateUnitMoves` only rises with the unit count
   (112-208 ms). What makes a spike a spike is GC: 23-36 gen-0 and 11-16 gen-1 collections (3-5 gen-2 at turns 198-200) and 78-145 ms of pause
   in those turns, so the follow-up's "spike coincides with a gen-2 GC" holds for the very last turns but not for turn 197. Run totals: 2479/590/36
   collections and 6.5 s of GC pause in 59.0 s of turn time (11%). The lighter third run had no late spike above 400 ms (slowest 434 ms, turn 1), so which
   turns are called spikes is load-dependent; the composition of a slow turn (refresh first, unit moves scaling with the unit count, GC pause as the
   amplifier) was the same in every run.

### Follow-ups filed from this profile

- **T406** worker improvement evaluation: cache `GetImprovementBuildingProblems` and `GetImprovementRanking` per turn (top non-pathfinding cost).
- **T407** `CannotPassThroughReason` allocates a `KotlinLazy<bool>` per call: hoist or cache it per unit/tech state.
- **T408** reduce BFS allocation in `GetShortestPath`: reuse the distance-to-tiles buffer across frontier expansions.
- **T409** cache `UnitAutomation.GetDangerousTiles` in the `AutomationCache` (30.8 ms per turn of `Unit.Civilian` self time).

## Result of T262

Method: golden per-turn `FullHash` list (`design/perf-golden.json`, small 50 and medium 200 turns) generated before any change; `bench --golden design/perf-golden.json` compares every turn and exits 1 on a difference (also when nothing was compared).
The list is frozen at the pre-T262 engine: after a deliberate engine change regenerate it.
`BenchTests.TheCommittedGoldenListStillMatchesTheEngine` checks the first 25 small turns against the real file on every gate run; the full small 50 and medium 200 lists are a manual check (`bench --scenario all --golden`, medium takes minutes), run after the final change: both identical.
All 8 after-runs (medium, 200 turns, two sets of 3 and 5 interleaved base/new pairs) matched all 200 golden hashes, and so did the base runs.

Numbers: medium, 200 turns, base and new alternated in one session on a loaded machine (wall times vary by 30% or more between runs; the spread is the main finding).

| metric | before | after | change |
|---|---|---|---|
| allocated (all 8 runs, spread under 2%) | 58.8-59.9 GB | 39.0-39.3 GB | **-34%** (robust) |
| user CPU, median of 5 / of 3 | 85.2 s / 99.3 s | 78.0 s / 81.7 s | -8% / -18% |
| total wall, median of 5 / of 3 | 87.0 s / 119.1 s | 71.3 s / 95.6 s | -18% / -20% |
| late median turn (last 20%), median of 5 / of 3 | 693 ms / 730 ms | 465 ms / 676 ms | -33% / -7% |
| late median turn, all 8 pairs pooled | 712 ms | 518 ms | -27% |

Late-median runs, base: 1431 730 363 538 496 762 693 796 ms; new: 1047 676 505 412 531 465 1142 385 ms.
Verdict against the ticket: the 30% late-median target was **not reliably met** (pooled -27%, individual sets -7% and -33%), the 10% fallback is met on the pooled and the 5-run late median and on total wall, but the late-turn wall figure is within the machine noise.
Allocation (-34%) and the GC pause that follows it are the robust gains; the CPU gain is smaller than the allocation gain.

What changed (all identical-state):
- `TileMap.GetViewableTiles`: neighbour lookup without per-call lists (oracle test against the old algorithm).
- `UniqueMap`/`TemporaryUnique`/`City`/`MapUnit`/`MultiFilter`: iterators instead of `SelectMany`, static lambdas with state tuples, loops instead of `Any(lambda)`, empty fast paths.
- `CityStats.TileStatsCurrent`: a city's tile stats are not recomputed in the second city loop of `GameInfo.UpdateCivilizationState` when the civ happiness, the city's `CurrentCityStats` object (replaced by every `Update`, read by countables) and the worked tiles are unchanged since the first loop, and only in rulesets without a countable conditional or for-every-countable modifier on a `[stat] Per Turn` countable (`Ruleset.HasCountableModifiers`: those read the stats of every city, which change between the loops). The bundled rulesets have none.

Rejected as not provably identical: civ-level dirty tracking of the refresh, skipping the duplicate `UpdateViewableTiles`/`UpdateCivResources` passes.

## Targets recorded for T262 and T263

- T262 (optimise): the top three hotspots are 1 (refresh), 2 (unit moves) and 3 (allocation); target **at least 30% off the late median turn time of the medium scenario**
  (fallback bound from the ticket: 10%), measured as the median of three runs in the same session before and after, with every per-turn hash equal to the golden list.
- T263 (parallelism): per-recipient view building (hotspot 6) and the per-civ refresh work are the first candidates; the engine turn itself is sequential by rule.

## Parallelism

T263 spike. Rules that frame it: engine logic is single-threaded (BannedSymbols forbids `Thread`, `Parallel`, `Task.Run(Action)`), every per-turn state hash must stay equal to `design/perf-golden.json`, and a candidate is only recommended with a deterministic merge order and a hash-equality proof. Result: **one go (host-side per-recipient view building, outside the engine, behind a default-off switch), everything inside the engine turn is no-go.**

### Method and prototype

The prototype is a patch kept as text, not merged: `design/T263-parallel-prototype.patch` (against `Unciv.Cli/BenchRunner.cs` and a `UCS.Civ` probe point in `GameInfo.UpdateCivilizationState`).
For the last 5 turns of the medium scenario, around every command it runs the host's per-recipient work (`ViewDeltaBuilder.Capture`, then `BuildWithDiff` + `ProtocolCodec.Encode`) twice from the same inputs: once sequentially (the path that feeds the next snapshot) and once with one task per recipient, alternating which goes first, and compares the encoded frames byte for byte.
The run also checks `--golden design/perf-golden.json` (exit 0 = all 200 per-turn hashes equal). Machine: 20 logical cores, **shared**, load average between 10 and 88 during the runs, so every number below is a spread, not a point.
Limits of this evidence: engine events are passed as an empty list in both paths (the per-event `EventFilter` paths are not compared); every lazy cache is already warm after the first command, so equal frames cannot show a cold-start lazy-initialisation race; per-thread probes mean the `View.*` phases are not attributed on the pool threads. 540 frames = 90 commands x 6 recipients.
(The bench had only 6 living majors at the end of the medium game, so "8 recipients" means 6.)

| Recipients | Runs | Frames compared | Byte mismatches | Golden hashes | Speed-up of capture+build+encode, parallel vs sequential (each run) | Median |
|---|---|---|---|---|---|---|
| 6 | 6 | 3240 | 0 | all equal | 1.33 2.18 2.04 1.46 1.99 1.33 | 1.73x |
| 3 | 3 | 810 | 0 | all equal | 1.38 1.03 1.05 | 1.05x |
| 1 | 2 | 180 | 0 | all equal | 0.75 0.85 | 0.80x |

Absolute per run (6 recipients, 90 commands): sequential 1.7-4.2 s, parallel 0.74-2.85 s. The gain is limited by the machine load (the other agents' builds took the cores) and by the allocation rate (the work allocates heavily, so threads contend on the GC); on an idle machine it should be higher, which was not measured. With one recipient there is nothing to parallelise and the task overhead makes it slower, so the switch must also fall back to the sequential path for fewer than two recipients.
Capture (before the command) alone gains little (0.5-2.5x, noisy); the snapshot/diff/encode part carries the gain.

### Candidates

1. **Per-recipient view building in `GameHost`** (`CaptureViewMarks`, `BroadcastDelta`: Capture, SnapshotBuilder.Build, SnapshotDiff.Compute, BuildEvents, Encode).
   - Shared mutable state: none written on the traced paths. It only reads the engine (GameView/TileMapView/CivView) and writes its own recipient's `PreviousSnapshot` (one recipient per task). Risk: engine getters that lazily fill caches (`??=`: 104 sites in 61 files of Unciv.Core; `KotlinLazy` itself is locked) could race when two recipients read the same object; no mismatch in 4230 frames, but that is warm-cache evidence, not proof. The audit (T296) must prove, over the whole call graph, that the view builders never reach the mutating members (`CityStats.Update`, `UpdateCivResources`, `TryUpdateAllCityStats` and other writers on CivView/CityView/TradeView), and must treat the `??=` initialisers they do reach (`Tile.Neighbors`, `IsAdjacentToCoastLazy`, `DiplomacyManager.OtherCiv`/`State`, the `City.CityStats` getter) as the forbidden or pre-warmed set; its guard test must start from a cold, freshly loaded game.
   - Merge order: results are collected into an array indexed by session position and sent in the existing `sessions.Values` order; each frame depends only on (state, recipient, seq), never on another recipient, so the order cannot change any frame (shown above: byte-equal frames).
   - Engine state hashes: untouched, because the work runs on the host actor between commands while the engine is idle (`GameHost` runs everything through its mailbox; `ApplyServerCommand` must only be called on the actor). The implementation must wait for all tasks before the actor continues; `BroadcastDelta` is synchronous, so that is a blocking `Task.WaitAll` on the actor thread. Golden hashes equal in all 11 runs.
   - Expected gain: view building is ~5.4 ms per recipient per command, ~110 ms/turn with one player and ~860 ms with eight; measured 1.3-2.2x with 6 recipients under load, so about 860 ms -> 400-650 ms at 8 recipients. No gain for one player.
Verdict: go (conditional: default-off switch, never for fewer than 2 recipients, preceded by the cache audit). It is no replacement for the incremental diff builder (T178 follow-up), which removes the cost instead of spreading it and also helps one player.
2. **Per-civ `UpdateCivilizationState` / `RefreshTransientCaches`** (hotspot 1).
   - Shared mutable state, concrete: `Tile.SetExplored` replaces the tile's `exploredBy` set (copy-on-write on a field shared by all civs, so two civs exploring the same tile lose an update); `CivInfoTransientCache.UpdateViewableTiles` calls `MakeCivilizationsMeet` which mutates both civs' diplomacy dictionaries and `metCiv.AddNotification` (another civ's notification list, order visible in state and hashes); `UpdateCivResources` reads the city states' state, and the code comment says major-civ happiness depends on the city states' resources, which is why `UpdateCivilizationState` processes city states first (a hard ordering edge, so at least two waves); `CapitalConnectionsFinder` reads (only) every civ's tiles, cities and diplomacy, which would race with the writes above; `CityStats.Update` can fire notifications and `ChooseNextConstruction`.
   - Merge order: would need a split into read-only compute per civ and a serial apply in civ-index order (city states first), i.e. a rewrite of the refresh, not a wrapper; a plain parallel loop cannot be hash-identical (lost updates and notification order).
   - Gain bound (Amdahl, measured last-quarter ms per turn of the `UCS.Civ` probe, summed over the 19 passes): majors 36 29 30 27 23 22 ms, city states 3-9 ms each, total about 225 ms per turn, longest civ 36 ms, so at most ~6x on those 225 ms (~35% of an engine turn) before the two-wave constraint, and each of the 19 passes per turn is a separate ~12 ms fork-join. The achievable gain is far below that, while T262-style de-duplication (322 per-civ passes instead of 17) removes the same time without threads.
Verdict: no-go. Revisit only after the refresh is deduplicated and split into compute and apply phases, and then only the read-only compute part.
3. **Per-unit visible-tile computation** (`UCS.UnitVisibleTiles`, 40 ms per turn, ~6%).
   - Read-only `GetViewableTilesList` could be computed in parallel and applied serially, but the apply (explored, meetings, triggered uniques) is the shared-state part above and the ceiling is ~6% of a turn.
Verdict: no-go (ceiling too low for the risk; an algorithmic cache, listed under follow-ups, is the better route).
4. **AI decisions per civ** (`AI.AutomateUnits` 156 ms, `AutomateUnitMoves` 142 ms per turn).
   - Each decision mutates the shared game immediately (unit moves, city founding, diplomacy) and later civs see the result; all of it draws from the one seeded RNG in call order. Speculative parallel evaluation would need re-validation of every decision against the changed state, which changes the outcome whenever it invalidates.
Verdict: no-go. A read-only sub-step (a pathfinding query within one unit's move) is possible but fine-grained and per-call dispatch cost exceeds it (0.64 ms per unit move).
5. **State hash computation** (`Hash.FullHash`: 5 ms per human turn start, under 2% of a turn).
Verdict: no-go (nothing to gain; serialising a live game concurrently with the engine would need a copy).
6. **Per-civ start/end of turn** (`Start.Cities` 47 ms, `End.Cities` 10 ms).
   - Share the RNG, notifications and the same cross-civ reads as 2.
Verdict: no-go.
7. **Map generation** (game start only, not turn work).
   - Consumes one RNG sequentially in step order; the spike did not measure it because it does not occur during a game.
Verdict: no-go.

### Ordered list of safe candidates and the risks

1. Cache-write audit plus a read-only guard for host view building (precondition). 2. Parallel per-recipient view building behind a default-off host option. Nothing else is safe today.
Risks: lazy `??=` caches read from several threads (audit), allocation pressure making the gain smaller than the core count suggests (GC settings, such as concurrent GC or server GC, are a host decision to measure with it), thread-pool starvation of the host's network tasks on a small server (cap the degree of parallelism), and the temptation to extend the pattern into the engine turn (excluded above).

### Cache-write audit of host view building (T296)

Question: does `ViewDeltaBuilder.Capture`, `ViewDeltaBuilder.BuildWithDiff` (which runs `SnapshotBuilder.Build`, `SnapshotDiff.Compute` and `BuildEvents`) or `SnapshotBuilder.Build` write any shared state? If several recipients are built on several threads, every lazy cache they fill is a data race. The 104 `??=` sites and every `KotlinLazy`, `Lazy` and `*Cache` field of `Unciv.Core` were triaged, and the reachable ones were then checked empirically.

**Empirical guard.** `ViewBuildingReadOnlyTests` fingerprints by reflection the instance fields of every object reachable from the `GameInfo` (private, transient and lazy fields included; collections by content; fields of framework types such as `Random` or `StringBuilder` are not walked; delegates only as set/unset) plus, for the engine assembly's non-generic types, the static primitives and strings by value, collections by count only, and `KotlinLazy` created state. It builds the views of every civ (Capture, Build, BuildWithDiff) once as the warm-up, builds them twice more, and requires the fingerprints and `ReplayVerifier.FullHash` to be identical. Scenarios: default, crowd, trade, espionage, alerts, rich, policies, victory, decisions (G&K), religion (G&K), skirmish, and five of them again from a save/load round trip (the coldest state a restarted host has). A companion test proves the fingerprint notices a single cache field being written. A failure names the first object that changed, which is the lazy site to fix.

What the warm-up round does write (the only writes observed, all in cold-loaded games): `KotlinLazy` unique maps and unique lists of ruleset objects (`Promotion`, `ModOptions`), and the cached `Values` wrapper of the `OrderedDictionary` inside `Counter<K>`. Nothing in the game state, no `FullHash` change.

| Site | Reached from view building | Verdict |
|---|---|---|
| `KotlinLazy<T>` (`RulesetObject`/`ModOptions` uniques and `UniqueMap`, `Terrain.IsCoast` and friends, `Policy`, `Victory`, `Speed`, `Quest`, `GameInfo.civMap`) | Yes (unique lookups on every view) | Leave: `KotlinLazy` is double-checked under a lock with a volatile publish. The initialisers only read the ruleset. |
| `Counter<K>.Values`/`SumValues` (BCL `OrderedDictionary.Values` creates its wrapper on first use) | Yes (observed) | Leave: the wrapper is a stateless view over the same dictionary, two racing creators both produce a valid object, a reference write is atomic. |
| `Terrain`, `Building`, `BaseUnit` `cachedMatchesFilterResult`, `CityConstructions.containedBuildingFiltersCache` (plain `Dictionary<string,bool>`, filled with any filter string the first time it is evaluated) | `BaseUnit` observed (invisibility checks); the others through unique conditionals, depending on the game state | **Fixed**: now `ConcurrentDictionary`. A warm-up cannot cover them (a filter string can first occur in a much later state), and an unlocked `Dictionary` written by two threads can corrupt. `LazilyKeyedFilterCaches_AreConcurrentDictionaries` pins the type. |
| `TileMap.TileUniqueMapCache`, `Countables` caches, `Expressions.Cache` | Possible | Leave: already `ConcurrentDictionary`. |
| `CityFocus.StatValuesCache` (static `Dictionary`) | Not by the builders (focus setting only) | Leave: already guarded by `lock`. |
| `City.CityStats` (`cityStats ??=`) | Yes (`GetStatMapForNextTurn` reads `FinalStatList`) | Leave: created and updated at load and at founding, never first-created by a builder (no write seen in any cold-load scenario). The guard fails if that changes. |
| `DiplomacyManager.otherCivCache` | Yes | Leave: set by `SetTransients`. |
| `DiplomacyManager.context` (`State`) | Possible | Leave: pure construction from two civs; last writer wins with an equivalent object. |
| `Tile.isAdjacentToCoastCache` (`bool?`), `Tile.tileResourceCache`, `TileStatFunctions.riverTerrainCache`, `Civilization.cachedMilitaryMight`, `BaseUnit.cachedForceEvaluation` | Not reached (probed with counters in all scenarios; `IsAdjacentToCoast` is only used by terrain generation and improvement rules) | Leave. If a builder ever starts to call one, warm it at `SetTransients`: a racing `bool?` read is not guaranteed atomic. |
| `BaseUnit` `isGreatPerson`, `isMilitary`, `isLandUnit`, `isWaterUnit` (`bool?`, `??=`) and `type ??=`; `Tile.neighbors` (`??=`); `Civilization.cityStateFunctions` (`??=`); `Religion.foundingCivCache`; `Specialist.colorObject` | Yes for `BaseUnit.IsMilitary` (`ForeignMapUnitView`) and `Tile.Neighbors` (every distance query); the rest possibly | Leave, by evidence rather than by proof: each caches a pure function of immutable ruleset or map data, so a racing duplicate computes the same value, and the reference-typed ones publish atomically. The guard saw no write to any of them after the warm-up in any scenario. The `bool?` ones carry the torn-read caveat of `Tile.isAdjacentToCoastCache` above; if one ever misbehaves, compute it eagerly at ruleset/`SetTransients` time. |
| `TemporaryUnique.UniqueObject`, `CityReligionManager.pressureFromAdjacentCitiesCache`, `CityStats` inner `??=` | Not reached by view building (turn processing and city updates only) | Leave. |
| Automation (`WorkerAutomation`, `RoadBetweenCitiesAutomation`, `UnitAutomation` caches, `Civ5AiState` `??=`), command `??=` sites (`GreatWorks`, `Archaeology`), `GameInfo.PlayerSettingsByCiv ??=`, `IdChecker`, `FileGameStore`, validators | No (engine turn, commands and persistence run on the one engine thread and never inside a view builder) | Leave. |
| `CivView.TryUpdateAllCityStats`, `CityView.UpdateCityStats`, `TradeView.UpdateCivResources` | Not called by the builders | Leave, but a parallel view builder must never call these mutating View members. |

Residual risk and rule for T263: the guard proves the exercised paths only. Any new lazy cache that view building can reach must either be filled at `SetTransients` (so the warm-up round finds it filled) or be safe for concurrent writers; the test turns a violation into a failure with the owning type and field named. The parallel build must also run after the command is applied and the game thread is idle (no engine writes during the fan-out), and the warm-up round must have run once on the game since load.

### Result of T297 (the go, implemented)

`HostLimits.ParallelViewBuilding` (default **off**) and `MaxViewParallelism` (default 4; below 2 the option is off). `GameHost.CaptureViewMarks` and `BroadcastDelta` collect the open recipients in `sessions.Values` order; with the option on and at least 2 recipients the capture, and the `BuildWithDiff` plus `ProtocolCodec.Encode`, run as strided tasks (`Task.Run(Func<int>)`, JUSTIFY comment) over at most `MaxViewParallelism` workers, the actor blocks in `Task.WaitAll` until all ended (the engine is idle), a fault is rethrown after every task finished, and the results are applied (`PreviousSnapshot`) and sent in the original order through the same send path (`SendFrameAsync`). The sequential path is unchanged. The engine is untouched. Wired by T302: `Unciv.Server --parallel-views N`, `ServerLobby(hostLimits:)` and `InProcessHostOptions.Limits`.

Proof of equal frames: `GameHostParallelViewTests` plays a scripted game with 2 to 4 human recipients (client commands, server `ForceEndTurn`, turn resolution; caps 2 and 4) once sequentially and once in parallel and requires every frame of every recipient to be byte-equal; one test repeats the parallel run 20x. `bench --golden design/perf-golden.json` with `--host-parallel 4`: "200 turn hashes identical" in all 3 runs. The T296 warm-up rule is enforced: the first view round of a host always runs sequentially (`viewsWarm`), and cold-load safety otherwise rests on the T296 audit table and the guard test. A task that throws no longer stops the others: the healthy recipients are applied and sent, then the first error is rethrown on the actor (a deliberate difference: the sequential loop stops at the failing recipient).

The cold-load rule of T296 applies unchanged (`ViewBuildingReadOnlyTests` guard); no new lazy cache was added.

Speed-up measurement, **inconclusive** (`bench --scenario medium --host-turns 5 --recipients 6 [--host-parallel 4]`, Release, 90 commands x 6 recipients, ms per command for capture + build + encode, alternating sequential/parallel; load average 1 min at the start of each run in brackets):

| Pair | Sequential | Parallel (4 tasks) | Speed-up |
|---|---|---|---|
| 1 | 19.6 (load 36) | 16.9 (load 19) | 1.16x |
| 2 | 13.5 (load 12) | 25.9 (load 7) | 0.52x |
| 3 | 44.7 (load 16) | 24.9 (load 54) | 1.80x |

Honest reading: the machine was shared and loaded (load average 7 to 54 on 20 logical cores) and the sequential time itself varies 3x between runs, so this bench cannot show a reliable gain; the median ratio is 1.16x and one pair is slower. The T263 prototype measured 1.3-2.2x with 6 recipients under similar load, from an in-run A/B on identical inputs, which is the better method; this run compares separate runs. Per-recipient phases (`View.*`) are not attributed in parallel mode, and the parallel path records one timed block where the sequential one records enter/exit per phase and recipient, so the comparison slightly favours the parallel side. A real gain needs an idle machine or a dedicated host; leave the option off until measured there.

## Result of T342 (incremental host views, implemented)

`HostLimits.IncrementalViewBuilding` is **on by default**; `Unciv.Server --no-incremental-views` turns it off. Per recipient the host keeps the last
`ClientSnapshot`, the canonical JSON element of each tile, each tile's map index, the map-content revision and the viewer's tech/policy reveal revision
(`ViewDeltaBuilder.ViewMark` now carries the latter two). After a command the builder:

- reuses every explored fogged tile by reference (a human's fog snapshot carries only remembered improvement/owner plus static terrain/resource, and those
  inputs are covered by the revisions; the base game writes a remembered value only while its tile is visible, and every such command rebuilds that tile);
- rebuilds the currently visible tiles, tiles that changed visibility, newly explored tiles (including one that a unit explored and left in the same
  command) and the positions named by the command's events, and compares the new `TileDto` to the old one field by field so an unchanged visible tile is
  reused too;
- rebuilds units from the visible tiles in map order (the same set and order the full build emits) and reuses an unchanged unit DTO;
- builds the rest of the snapshot (cities, civs, diplomacy, notifications, ...) the same way as the full build, but computes the view hash from the cached
  per-tile canonical JSON instead of serializing every tile again;
- computes the `SnapshotDiff` from the rebuilt keys (tiles can never reorder; unit order is derived exactly as the full diff does).

Anything it cannot prove safe falls back to the full build and re-seeds the state: a non-human or spectator viewer (whose fog view shows live improvement),
a stale/foreign previous snapshot, a map-content revision change (terrain, features, resource/site placement), or a technology/policy count change that can
reveal a resource on a fogged tile. The fallback is covered by `IncrementalViewBuilderTests` (each condition) and by the host test that drops the state
between commands; the client's existing resync path still catches a diff applied to the wrong base (`SnapshotDiffTests`). The one documented invariant is
that no caller writes a human viewer's remembered improvement/owner for an explored, fogged tile without also bumping a revision the builder watches; the
base game only writes them while the tile is visible (whose command rebuilds it), so only a mod/future unique needs to honour that.

The rebuilt-tile set and the diff key tiles through the same `SnapshotDiff.TileKey(int,int)` helper (invariant culture); the regression test changes a
visible tile at a negative coordinate under a hostile ambient culture (a different `NegativeSign`) and compares the full and incremental diff and the
client mirror hash, the bug class `EndToEndDeterminismTests.HexCoordTextIsCultureIndependent` pins for `HexCoord` text.

Proof of equal output: `IncrementalViewBuilderTests.ScriptedLateGameCommands_ProduceByteIdenticalSnapshotsAndDiffs` drives a scripted two-human game and
compares every command's full and incremental snapshot (canonical JSON), diff, events and view hash, applying the incremental diff to a client mirror;
`GameHostParallelViewTests.IncrementalFrames_EqualTheFullBuildFrames` (2/4 recipients, 3/6 rounds), `IncrementalWithParallelViews_EqualsTheFullBuild` and
`LostIncrementalState_FallsBackToTheFullBuild_...` compare every encoded host frame with the full-build run. `bench --golden design/perf-golden.json`
after the change: small 50 and medium 200 turn hashes identical (the engine is untouched; the revisions are transient).

Measurement: `bench --scenario six --turns 200 --host-turns 5 --recipients 3 --host-views full|incremental --repeat 1`, Release, alternating full and
incremental in one session (the machine is shared; four interleaved runs). The six scenario is the ticket's late-game table: 6 majors with **3 human seats**
(Babylon is scripted, the other two take their turns without actions), 6 city states, Medium map, 22 cities and ~195 units at turn 200; the last 5 turns
carry 90 commands x 3 recipients = 270 view builds per run. Host-side times only (the bench subtracts them from the engine numbers):

| Run | View mode | `Host.Capture` + `Host.BuildDelta` + `Host.Encode` per turn | `Host.BuildDelta` per recipient-command | Incremental / full builds |
|---|---|---|---|---|
| 1 | full | 63.6 ms | 1.037 ms | 0 / 270 |
| 2 | full | 64.4 ms | 1.065 ms | 0 / 270 |
| 3 | incremental | 49.1 ms | 0.803 ms | 267 / 3 |
| 4 | incremental | 46.8 ms | 0.757 ms | 267 / 3 |
| median | full -> incremental | **64.0 -> 48.0 ms (-25%)** | **1.051 -> 0.780 ms (-26%)** | |

Review re-run (T342 changes-requested, shared-culture tile key): four alternating single runs each (`--host-views full|incremental`), Release, same scenario.
`Host.Capture + Host.BuildDelta + Host.Encode` median **1.252 -> 0.967 ms per recipient-command** and **67.6 -> 52.2 ms per turn** for 3 recipients (-23%);
`Host.BuildDelta` median **1.103 -> 0.849 ms** (-23%); 267/270 incremental with 3 full builds in every incremental run. All eight runs ended in the same
state (`turn=200 civs=13 cities=22 units=196`), and the engine late median was 171.0 (full) vs 173.8 ms/turn (incremental), unchanged within the shared
machine's noise. `bench --golden` after the fix: small 50 and medium 200 turn hashes identical.

The three full builds are the deliberate reveal-revision fallback (a measured human finishes a tech in the window). The engine late median is unchanged
within noise (160.8 and 167.0 ms/turn for full, 164.1 and 165.7 for incremental; it is the same engine and the host cost is excluded by the harness). Adding
the measured host cost back to the late median gives about 228 ms -> 213 ms per turn for these 3 connected players (about -7% wall time); the gain grows with
the number of connected humans and with the explored fraction of the map, and is absent when there is one recipient or when the fallback runs. The host stays
sequential here; incremental view building composes with `--parallel-views` (tested for equal frames).

## Follow-ups

- ~~Call-level profiling inside `AI.AutomateUnitMoves` and the spike turns (install `dotnet-trace`; or add `Measure` points in pathfinding), and a GC-count column per phase.~~
  Done in T351: see [T351](#t351-call-level-breakdown-of-ai-unit-moves); the worthwhile fixes are T406-T409.
- ~~Incremental host view diff (T178 note), once the host cost per recipient is chosen as a target.~~
  Done in T342: see [Result of T342](#result-of-t342-incremental-host-views-implemented).
- Per-unit `GetViewableTiles` cache keyed on position, range and a terrain revision counter (still the top allocator after T262), and a struct `ViewableTile`.
- Remaining refresh cost: civ-level dirty tracking needs a proof that every input to `UpdateCivilizationState` is covered by a revision counter.
