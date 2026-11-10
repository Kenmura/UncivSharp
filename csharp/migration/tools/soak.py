#!/usr/bin/env python3
"""T341: run the recurring headless soaks (the env-gated Category=Soak tests) and, when one fails, print a ticket
note that carries the exact seed and the command that reproduces it.

The soak matrix lives with the tests so it cannot drift from what they play:
  Unciv.Core.Tests/Determinism/SoakTests.cs          T167  Gods & Kings, Small and Medium maps, 300 turns
  Unciv.Core.Tests/Determinism/BnwSoakTests.cs       B026  Brave New World, save/reload every 25 turns and a full replay
  Unciv.Core.Tests/Determinism/RulesetSoakTests.cs   T341  Vanilla and the bundled mods, save/reload every 10 turns

Every configuration prints a `SOAK CONFIG ... gameSeed=N mapSeed=N turns=N saveEvery=N` line before it starts, so the
failure note below can name the exact seeds even when several configurations ran in one process.

  (no args)             run every soak class (UNCIV_SOAK=1, --filter Category=Soak)
  --quick               run the short variants that run in every build too (no UNCIV_SOAK, RulesetSoakTests only)
  --class NAME ...      run only soak classes whose full name contains one of the terms (RulesetSoakTests, BnwSoakTests, ...)
  --seed N              forward UNCIV_SOAK_SEED=N (RulesetSoakTests uses it for both the game and the map seed)
  --turns N             forward UNCIV_SOAK_TURNS=N (a shorter representative run)
  --save-every N        forward UNCIV_SOAK_SAVE_EVERY=N (a checkpoint interval other than the default)
  --list                print the classes and the environment knobs, run nothing
  --no-build            pass --no-build to `dotnet test` (the gate or a previous run already built)
  --log FILE            also write the complete output to FILE

Run it from anywhere: python3 -I csharp/migration/tools/soak.py [options]. Like gate.py/ptest.py it enters the
dotnet-dev container when dotnet is not on the host, caps MSBuild nodes and removes its temp folder (T390).
"""
import argparse
import os
import re
import shlex
import shutil
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import runtmp  # noqa: E402  (explicit path: -I drops the script directory from sys.path)

MIGRATION = os.path.dirname(HERE)
CSHARP = os.path.dirname(MIGRATION)
CONTAINER = "dotnet-dev"
CORE = "Unciv.Core.Tests"

SOAK_CLASSES = {
    "SoakTests": "T167: Gods & Kings, Small/Medium maps, 300 turns, determinism across two runs",
    "BnwSoakTests": "B026: Brave New World, save/reload every 25 turns plus a full command-log replay",
    "RulesetSoakTests": "T341: Vanilla ruleset and bundled mods, save/reload every 10 turns",
}

CONFIG = re.compile(r"SOAK CONFIG .*?gameSeed=(?P<game>-?\d+) mapSeed=(?P<map>-?\d+) turns=(?P<turns>\d+) saveEvery=(?P<save>\d+)")
FAILED_TEST = re.compile(r"(?:^|\s)(?:Failed|FAILED)\s+([A-Za-z0-9_.]+)")


def env_pairs(extra):
    """Environment every dotnet command of the tools runs with (T360), plus the soak overrides."""
    pairs = ["MSBUILDDISABLENODEREUSE=1", "MSBUILDNODECOUNT=1", "MSBUILDUSESERVER=0",
             "UseSharedCompilation=false", "DOTNET_gcServer=0", "DOTNET_CLI_TELEMETRY_OPTOUT=1"]
    if os.environ.get(runtmp.ENV_DIR):  # T390: the test hosts' temp files go in the run folder removed on exit
        pairs.append("TMPDIR=" + os.environ[runtmp.ENV_DIR])
    pairs += ["%s=%s" % (k, v) for k, v in extra.items()]
    return pairs


def dotnet_cmd(args, extra_env):
    """argv (or a shell string through distrobox) that runs `dotnet <args>` from csharp/."""
    full = ["env"] + env_pairs(extra_env) + ["dotnet"] + args
    if shutil.which("dotnet"):
        return full, CSHARP
    inner = "cd %s && %s" % (shlex.quote(CSHARP), " ".join(shlex.quote(a) for a in full))
    return ["distrobox", "enter", CONTAINER, "--", "bash", "-c", inner], None


def plan(classes, quick, seed, turns, save_every, no_build):
    """What to run: the test filter, the forwarded environment and the dotnet arguments (unit-tested, no process)."""
    if classes:
        flt = "|".join("FullyQualifiedName~%s%s" % (c, "." if c in SOAK_CLASSES else "") for c in classes)
    elif quick:
        flt = "FullyQualifiedName~RulesetSoakTests."
    else:
        flt = "Category=Soak"
    env = {}
    if not quick:
        env["UNCIV_SOAK"] = "1"
    if seed is not None:
        env["UNCIV_SOAK_SEED"] = str(seed)
    if turns is not None:
        env["UNCIV_SOAK_TURNS"] = str(turns)
    if save_every is not None:
        env["UNCIV_SOAK_SAVE_EVERY"] = str(save_every)
    args = ["test", CORE]
    if no_build:
        args.append("--no-build")
    args += ["--filter", flt, "--logger", "console;verbosity=detailed", "-m:1", "-nodeReuse:false", "-p:UseSharedCompilation=false"]
    return flt, env, args


def configurations(lines):
    """The SOAK CONFIG lines of the output, de-duplicated in order (a failed class may print several)."""
    seen, found = set(), []
    for line in lines:
        match = CONFIG.search(line)
        if not match:
            continue
        key = (match.group("game"), match.group("map"), match.group("turns"), match.group("save"))
        if key in seen:
            continue
        seen.add(key)
        # Keep the beginning of the line (the configuration name) for the ticket.
        found.append((line.split("SOAK CONFIG", 1)[0].strip(" []") or "soak", match))
    return found


def failed_test(lines):
    for line in lines:
        match = FAILED_TEST.search(line)
        if match and not match.group(1).isdigit():
            return match.group(1)
    return None


def soak_class(test):
    """The soak class of a failed test's full name (RulesetSoakTests, BnwSoakTests, ...), for --class."""
    parts = test.split(".")
    for known in SOAK_CLASSES:
        if known in parts:
            return known
    return parts[-2] if len(parts) >= 2 else "RulesetSoakTests"


def ticket_block(lines, command, filter_text, seed, turns, save_every, exit_code):
    """The note a failure must be filed with: every configuration that ran, its seeds, and the reproduce command."""
    runs = configurations(lines)
    rows = []
    for name, match in runs:
        rows.append("  %s: gameSeed=%s mapSeed=%s turns=%s saveEvery=%s"
                    % (name, match.group("game"), match.group("map"), match.group("turns"), match.group("save")))
    if not rows:
        rows.append("  (no SOAK CONFIG line: seed=%s turns=%s saveEvery=%s)"
                    % (seed if seed is not None else "default", turns if turns is not None else "default",
                       save_every if save_every is not None else "default"))
    test = failed_test(lines) or "<the failing test shown above>"
    first_seed = runs[0][1].group("game") if runs else seed
    first_turns = runs[0][1].group("turns") if runs else turns
    first_save = runs[0][1].group("save") if runs else save_every
    reproduce = ["python3", "-I", "tools/soak.py", "--class", soak_class(test)]
    if first_seed is not None:
        reproduce += ["--seed", str(first_seed)]
    if first_turns is not None:
        reproduce += ["--turns", str(first_turns)]
    if first_save is not None:
        reproduce += ["--save-every", str(first_save)]
    return "\n".join([
        "SOAK FAILED (exit %d, filter %s)." % (exit_code, filter_text),
        "Configurations that ran:",
        *rows,
        "Reproduce exactly:",
        "  %s" % " ".join(reproduce),
        "  (or: %s)" % (" ".join(shlex.quote(a) for a in command) if command else "see the command above"),
        "File a ticket with:",
        "  Title: Soak failure: %s" % test,
        "  Notes: command `%s`; seed(s) above; first divergence copied from the log. T341 requires failures to be filed with the seed."
        % " ".join(reproduce),
    ])


def run(argv, cwd, log_path):
    lines = []
    log = open(log_path, "w", encoding="utf-8") if log_path else None
    try:
        proc = subprocess.Popen(argv, cwd=cwd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, bufsize=1)
        assert proc.stdout is not None
        for line in proc.stdout:
            sys.stdout.write(line)
            sys.stdout.flush()
            lines.append(line.rstrip("\n"))
            if log:
                log.write(line)
        return proc.wait(), lines
    finally:
        if log:
            log.close()


def run_main(a):
    if a.list:
        print("soak classes:")
        for name, description in SOAK_CLASSES.items():
            print("  %-18s %s" % (name, description))
        print("environment: UNCIV_SOAK_SEED (RulesetSoakTests seeds), UNCIV_SOAK_TURNS, UNCIV_SOAK_SAVE_EVERY")
        print("the full matrix and its default seeds are declared in RulesetSoakTests.Configs; every run prints SOAK CONFIG ...")
        return 0
    classes = a.classes or []
    if a.quick and classes:
        print("soak: --quick ignores --class", file=sys.stderr)
        return 2
    flt, env, args = plan(classes, a.quick, a.seed, a.turns, a.save_every, a.no_build)
    argv, cwd = dotnet_cmd(args, env)
    print("soak: %s" % (" ".join(argv) if cwd else " ".join(args)), flush=True)
    code, lines = run(argv, cwd, a.log)
    if code != 0:
        print(ticket_block(lines, argv, flt, a.seed, a.turns, a.save_every, code), file=sys.stderr)
    else:
        print("soak: ok", flush=True)
    return 0 if code == 0 else 1


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--class", dest="classes", nargs="+", metavar="NAME")
    ap.add_argument("--quick", action="store_true", help="run the short always-on variants (no UNCIV_SOAK)")
    ap.add_argument("--seed", type=int)
    ap.add_argument("--turns", type=int)
    ap.add_argument("--save-every", type=int, dest="save_every")
    ap.add_argument("--list", action="store_true")
    ap.add_argument("--no-build", action="store_true")
    ap.add_argument("--log")
    a = ap.parse_args(argv)
    runtmp.acquire()
    try:
        if not runtmp.require_space():
            return 1
        return run_main(a)
    finally:
        runtmp.release()


if __name__ == "__main__":
    sys.exit(main())
