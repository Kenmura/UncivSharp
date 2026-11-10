using Unciv.Logic;
using Unciv.Logic.Map;

namespace Unciv.Core.Tests.Determinism;

// T341: the shapes of the ruleset soak matrix, kept out of RulesetSoakTests.cs so ptest's affected selection (which treats every
// declared type in a file that holds a test as a test class) does not run two empty test processes for them.
internal sealed record RulesetSoakConfig(string Name, string Ruleset, string[] Mods, string[] Nations, MapSize Size, string Type,
    int Majors, int CityStates, long GameSeed, int MapSeed, int Turns, int SaveEvery);

internal sealed record RulesetSoakResult(RulesetSoakConfig Config, int Turns, string? Winner, double MaxTurnSeconds, double TotalSeconds,
    List<ulong> Hashes, GameInfo Game);
