"""Tests for T341's soak runner: the command plan, the forwarded overrides and the failure note that carries the seed.
Run: python3 -I -m unittest discover -s tools -p 'test_*.py' (from csharp/migration)."""
import contextlib
import importlib.util
import io
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent

spec = importlib.util.spec_from_file_location("soak", HERE / "soak.py")
soak = importlib.util.module_from_spec(spec)
spec.loader.exec_module(soak)


class Plan(unittest.TestCase):
    def test_default_runs_every_soak_class(self):
        flt, env, args = soak.plan([], False, None, None, None, False)
        self.assertEqual(flt, "Category=Soak")
        self.assertEqual(env, {"UNCIV_SOAK": "1"})
        self.assertEqual(args[:2], ["test", "Unciv.Core.Tests"])
        self.assertIn("--filter", args)
        self.assertNotIn("--no-build", args)

    def test_quick_runs_only_the_always_on_short_variants(self):
        flt, env, args = soak.plan([], True, None, None, None, False)
        self.assertEqual(flt, "FullyQualifiedName~RulesetSoakTests.")
        self.assertEqual(env, {})  # without UNCIV_SOAK only the [Fact] short variants run
        self.assertIn("Unciv.Core.Tests", args)

    def test_classes_are_matched_with_a_trailing_dot_so_soaktests_does_not_select_bnwsoaktests(self):
        flt, _, _ = soak.plan(["SoakTests", "BnwSoakTests"], False, None, None, None, True)
        self.assertEqual(flt, "FullyQualifiedName~SoakTests.|FullyQualifiedName~BnwSoakTests.")
        _, _, args = soak.plan(["SoakTests"], False, None, None, None, True)
        self.assertIn("--no-build", args)

    def test_a_test_method_term_is_not_given_a_trailing_dot(self):
        flt, _, _ = soak.plan(["Bnw300TurnsEightCivsSavesAndReplays"], False, None, None, None, False)
        self.assertEqual(flt, "FullyQualifiedName~Bnw300TurnsEightCivsSavesAndReplays")

    def test_overrides_are_forwarded_to_the_ruleset_soak(self):
        _, env, _ = soak.plan(["RulesetSoakTests"], False, 123, 60, 5, False)
        self.assertEqual(env, {"UNCIV_SOAK": "1", "UNCIV_SOAK_SEED": "123", "UNCIV_SOAK_TURNS": "60", "UNCIV_SOAK_SAVE_EVERY": "5"})

    def test_the_dotnet_environment_carries_the_no_idle_node_settings(self):
        pairs = soak.env_pairs({"UNCIV_SOAK": "1"})
        for expected in ("MSBUILDDISABLENODEREUSE=1", "MSBUILDUSESERVER=0", "UseSharedCompilation=false", "UNCIV_SOAK=1"):
            self.assertIn(expected, pairs)


class FailureNote(unittest.TestCase):
    LINES = [
        "[vanilla] SOAK CONFIG ruleset=Civ V - Vanilla mods=[] map=Small/SmallContinents majors=6 cityStates=6 gameSeed=37 mapSeed=113 turns=300 saveEvery=10",
        "[vanilla] turn 250: save/reload hash 1234 identical",
        "[bundled-mods] SOAK CONFIG ruleset=Civ V - Vanilla mods=[golden-mod,LateMod] map=Small/SmallContinents majors=6 cityStates=6 gameSeed=41 mapSeed=217 turns=300 saveEvery=10",
        "  Failed Unciv.Core.Tests.Determinism.RulesetSoakTests.BundledMods300TurnsSavesAndReloadsAtManyPoints [1 s]",
    ]

    def test_names_every_seed_and_the_reproduce_command(self):
        text = soak.ticket_block(self.LINES, ["dotnet", "test"], "Category=Soak", None, None, None, 1)
        self.assertIn("SOAK FAILED (exit 1", text)
        self.assertIn("vanilla: gameSeed=37 mapSeed=113 turns=300 saveEvery=10", text)
        self.assertIn("bundled-mods: gameSeed=41 mapSeed=217 turns=300 saveEvery=10", text)
        self.assertIn("Soak failure: Unciv.Core.Tests.Determinism.RulesetSoakTests.BundledMods300TurnsSavesAndReloadsAtManyPoints", text)
        self.assertIn("--class RulesetSoakTests --seed 37 --turns 300 --save-every 10", text)

    def test_without_a_config_line_it_falls_back_to_the_forwarded_values(self):
        text = soak.ticket_block(["Failed Unciv.Core.Tests.Determinism.SoakTests.SmallMapSixCivsSixCityStates300Turns"],
                                 ["dotnet", "test"], "Category=Soak", 11, 60, 5, 1)
        self.assertIn("seed=11 turns=60 saveEvery=5", text)
        self.assertIn("--class SoakTests --seed 11 --turns 60 --save-every 5", text)


class Cli(unittest.TestCase):
    def test_list_prints_the_soak_classes_and_runs_nothing(self):
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            self.assertEqual(soak.main(["--list"]), 0)
        text = out.getvalue()
        for name in ("SoakTests", "BnwSoakTests", "RulesetSoakTests"):
            self.assertIn(name, text)
        self.assertIn("UNCIV_SOAK_SEED", text)

    def test_quick_with_a_class_is_a_usage_error(self):
        with contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(soak.main(["--quick", "--class", "SoakTests"]), 2)


if __name__ == "__main__":
    unittest.main()
