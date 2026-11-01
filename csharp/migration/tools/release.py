#!/usr/bin/env python3
"""Build the CLI and the Godot client from the current (clean) tree and publish them to <repo>/csharp-releases/.

Usage: release.py T### [--cli-only | --client-only] [--linux-only] [--no-android] [--keep N] [--force] [--dry-run] [--keep-tmp]

ticket.py runs this after every `state T### Done`. The folder sits next to `csharp/` in the main checkout (never inside a worktree, which is
removed when its agent finishes):

    csharp-releases/<ticket>-<shortsha>/cli/        dotnet publish of Unciv.Cli, self-contained linux-x64 (runtime and data/jsons bundled: ./Unciv.Cli works with no dotnet installed)
    csharp-releases/<ticket>-<shortsha>/server-linux-x64/ and server-win-x64/   dotnet publish of Unciv.Server (T266), self-contained, data/jsons bundled (--cli-only builds them too).
                                                       Only the Linux one is run (start, LISTENING line, SIGTERM, exit 0); the Windows one is UNVERIFIED and its failure only warns
    csharp-releases/<ticket>-<shortsha>/relay-linux-x64/ and relay-win-x64/   dotnet publish of Unciv.Relay (T366), self-contained, with README.md, SETUP.md and deploy/ (no data/jsons: the relay reads no rulesets).
                                                       Only the Linux one is run (--help, start with --tls none, LISTENING tunnel line, SIGTERM, exit 0); the Windows one is UNVERIFIED and its failure only warns
    csharp-releases/<ticket>-<shortsha>/client-linux/   Unciv.Godot/tools/package.sh linux (self-contained: Godot export with the .NET runtime, data/jsons, fonts, LICENSE, notices)
    csharp-releases/<ticket>-<shortsha>/client-windows/ and client-macos/   the same for the other platforms (T258; --linux-only skips them). Only the Linux build is ever run
                                                       (smoke-export.sh in CI); Windows and macOS are checked for file presence and size (check_package.py) and are UNVERIFIED otherwise.
    csharp-releases/<ticket>-<shortsha>/client-android/Unciv-client-android-arm64.apk   package.sh android (T391): the debug-signed arm64 APK, checked like release_assets.py verify
                                                       (a zip holding the arm64 Godot library). NOT device-tested (T278). Optional: with no Android SDK / JDK / export templates (or any build
                                                       failure) it is left out with a WARNING and the release goes on. --linux-only, --cli-only and --no-android skip it.
    csharp-releases/<ticket>-<shortsha>/manifest.json   ticket, commit, tree, build time, what was built
    csharp-releases/latest -> newest version (only moves forward: a build of a commit older than the current latest never replaces it)
                                                       T403: and only to a release that holds client-android (the owner needs an APK from latest at all times). A release without it is still
                                                       published but latest stays, and release.py exits 1; --no-android is the explicit opt-out. package.sh android is retried after a Godot crash like the desktop clients.
    csharp-releases/releases.log                         one line per release

The newest --keep (default 5) versions are kept, plus whatever `latest` points at and the newest version with a good APK (T403). A rebuild (--force, or a retry) of an existing release folder never replaces one that has an APK by a build without one. Builds run in the dotnet-dev container when `dotnet` is not on PATH, like the Green gate.
The folder is git-ignored. A tree that was already released (same git tree id) is not rebuilt unless --force.
"""
import fcntl
import json
import os
import shlex
import shutil
import subprocess
import sys
import tempfile
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent.parent  # csharp/migration
CSHARP = HERE.parent
CONTAINER = "dotnet-dev"
KEEP_DEFAULT = 5
RID = "linux-x64"
SERVER_RIDS = ["linux-x64", "win-x64"]  # T266: release_assets.SERVER_RIDS (a unit test keeps them equal)
RELAY_RIDS = ["linux-x64", "win-x64"]  # T366: release_assets.RELAY_RIDS (a unit test keeps them equal)
CLIENT_PLATFORMS = ["linux", "windows", "macos"]  # T258: default client platforms; T279 builds the same list, in the same order, as release archives
ANDROID_PLATFORM = "android"  # T391: optional extra client (package.sh android -> build/android/Unciv.apk); not a desktop archive, so not in CLIENT_PLATFORMS
ANDROID_MISSING_EXIT = 3  # package.sh android exits 3 when the SDK / JDK / export templates are missing


def git(*args, check=True):
    r = subprocess.run(["git", "-C", str(HERE), *args], capture_output=True, text=True)
    if check and r.returncode:
        die(f"git {' '.join(args)} failed: {r.stderr.strip()}")
    return r


def die(msg, code=1):
    print(f"release: {msg}", file=sys.stderr)
    sys.exit(code)


def releases_root():
    """<main checkout>/csharp-releases, found through the shared .git directory so every worktree writes to the same place."""
    common = Path(git("rev-parse", "--path-format=absolute", "--git-common-dir").stdout.strip())
    return common.parent / "csharp-releases"


def in_container(cmd):
    """Wrap a shell command for dotnet-dev when dotnet is not on this PATH."""
    if shutil.which("dotnet") is not None:
        return cmd
    return f"distrobox enter {CONTAINER} -- bash -c {shlex.quote(cmd)}"


DOTNET = "env MSBUILDDISABLENODEREUSE=1 MSBUILDNODECOUNT=1 MSBUILDUSESERVER=0 UseSharedCompilation=false DOTNET_gcServer=0 dotnet"  # T360: same memory settings as ptest.dotnet_env()
CRASH_CODES = (139, 134, 132, -11, -6, -4)  # SIGSEGV / SIGABRT / SIGILL (the Godot headless export crashed with 132 once), as a shell reports them (128+n) or as a raw signal
CRASH_RETRIES = 2


def clear_import_cache():
    """Drop Godot's imported/exported caches so a retry re-imports from the sources (a crash can leave them half written)."""
    for sub in ("imported", "exported"):
        shutil.rmtree(CSHARP / "Unciv.Godot" / ".godot" / sub, ignore_errors=True)


def run(name, cmd, crash_retries=0):
    """Run a shell command. A crash exit (segfault/abort) is retried up to `crash_retries` times after clearing the Godot import cache:
    the headless export intermittently segfaults in Godot's threaded font import (~1 in 15 fresh imports, more under load)."""
    for attempt in range(crash_retries + 1):
        print(f"release: {name} ..." + (f" (retry {attempt} after a crash)" if attempt else ""), flush=True)
        r = subprocess.run(cmd, shell=True, capture_output=True, text=True)
        if r.returncode in CRASH_CODES and attempt < crash_retries:
            print(f"release: {name} crashed (exit {r.returncode}); clearing the Godot import cache and retrying", file=sys.stderr)
            clear_import_cache()
            continue
        break
    if r.returncode:
        print("\n".join((r.stdout + r.stderr).splitlines()[-30:]), file=sys.stderr)
        die(f"{name} failed (exit {r.returncode})" + (f" after {crash_retries + 1} attempts" if r.returncode in CRASH_CODES and crash_retries else ""))


def build_android(stage):
    """T391: build the debug-signed arm64 APK and copy it to stage/client-android. Returns True when it was built and checked; False (with a WARNING) when
    the Android prerequisites are missing or the build fails: the APK is optional and never fails a release."""
    cmd = in_container(f"cd {shlex.quote(str(CSHARP))} && Unciv.Godot/tools/package.sh {ANDROID_PLATFORM}")
    r = None
    for attempt in range(CRASH_RETRIES + 1):  # T403: like the desktop clients, a Godot crash (exit 139 once) is retried after clearing the import cache
        print(f"release: package client ({ANDROID_PLATFORM}) ..." + (f" (retry {attempt} after a crash)" if attempt else ""), flush=True)
        r = subprocess.run(cmd, shell=True, capture_output=True, text=True)
        if r.returncode in CRASH_CODES and attempt < CRASH_RETRIES:
            print(f"release: package client ({ANDROID_PLATFORM}) crashed (exit {r.returncode}); clearing the Godot import cache and retrying", file=sys.stderr)
            clear_import_cache()
            continue
        break
    if r.returncode:
        print("\n".join((r.stdout + r.stderr).splitlines()[-30:]), file=sys.stderr)
        why = "the Android SDK / JDK / export templates are missing" if r.returncode == ANDROID_MISSING_EXIT else f"the build failed (exit {r.returncode})"
        print(f"release: WARNING android client left out of this release: {why} (see Unciv.Godot/tools/install-export-templates.sh; the other clients are unaffected)", file=sys.stderr)
        return False
    assets = _assets()
    apk = CSHARP / "Unciv.Godot" / "build" / "android" / "Unciv.apk"
    out = stage / "client-android"
    out.mkdir()
    try:
        shutil.copy2(apk, out / assets.ANDROID_APK)
    except OSError as e:  # package.sh exited 0 but left no APK: still only a warning
        shutil.rmtree(out, ignore_errors=True)
        print(f"release: WARNING android client left out of this release: no APK produced ({e})", file=sys.stderr)
        return False
    problems = assets.check_apk(out / assets.ANDROID_APK)
    if problems:
        shutil.rmtree(out, ignore_errors=True)
        print(f"release: WARNING android client left out of this release: {problems[0]}", file=sys.stderr)
        return False
    return True


def _assets():
    import importlib.util

    spec = importlib.util.spec_from_file_location("release_assets", HERE / "tools" / "release_assets.py")
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def smoke_cli(cli_dir):
    """The published CLI must run with no dotnet on PATH, from a folder outside any checkout: start a tiny game with it."""
    with tempfile.TemporaryDirectory() as work:
        env = {"PATH": "/usr/bin:/bin", "HOME": work, "DOTNET_ROOT": "", "DOTNET_gcServer": "0"}
        save = Path(work) / "smoke.json"
        r = subprocess.run([str(cli_dir / "Unciv.Cli"), "new", "--out", str(save), "--map-size", "Tiny"], cwd=work, env=env, capture_output=True, text=True)
        if r.returncode or not save.exists():
            print((r.stdout + r.stderr)[-1500:], file=sys.stderr)
            die(f"the published CLI does not run on its own (exit {r.returncode})")


def smoke_server(server_dir):
    """The published Linux server must run with no dotnet on PATH: --help, start (LISTENING line), SIGTERM, exit 0 (release_assets.smoke_server)."""
    _smoke("smoke_server", server_dir)


def smoke_relay(relay_dir):
    """As smoke_server for Unciv.Relay (T366): --help, start with --tls none (LISTENING tunnel line), SIGTERM, exit 0 (release_assets.smoke_relay)."""
    _smoke("smoke_relay", relay_dir)


def _smoke(function, directory):
    import importlib.util

    spec = importlib.util.spec_from_file_location("release_assets", HERE / "tools" / "release_assets.py")
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    problems = getattr(mod, function)(directory)
    if problems:
        die(problems[0])


def versions(root):
    """Version folders, oldest first (by the build time in the manifest, else mtime)."""
    found = []
    for d in root.iterdir() if root.exists() else []:
        m = d / "manifest.json"
        if d.is_dir() and not d.is_symlink() and m.exists():
            try:
                found.append((json.loads(m.read_text()).get("built", 0), d))
            except ValueError:
                found.append((m.stat().st_mtime, d))
    return [d for _, d in sorted(found, key=lambda x: (x[0], x[1].name))]


def has_apk(d):
    """T403: True when release folder `d` lists client-android in its manifest and the APK file there passes release_assets.check_apk (a zero-byte or corrupt file does not count)."""
    try:
        if "client-android" not in json.loads((d / "manifest.json").read_text()).get("contents", []):
            return False
        assets = _assets()
        return not assets.check_apk(d / "client-android" / assets.ANDROID_APK)
    except (OSError, ValueError):
        return False


def prune(root, keep):
    """Delete all but the newest `keep` versions; whatever `latest` points at and the newest version that has a good APK always stay (T403: the owner needs an APK at all times,
    even when --no-android releases push the last APK build out of the newest `keep`). Returns the deleted names."""
    latest = (root / "latest").resolve() if (root / "latest").exists() else None
    vs = versions(root)
    newest_apk = next((d.resolve() for d in reversed(vs) if has_apk(d)), None)
    doomed = [d for d in vs[:-keep] if d.resolve() not in (latest, newest_apk)] if keep > 0 and len(vs) > keep else []
    for d in doomed:
        shutil.rmtree(d)
    return [d.name for d in doomed]


def is_newer(new_sha, old_sha):
    """True when `latest` may move to new_sha: there is no latest, or new_sha is not strictly older than old_sha (an ancestor of it)."""
    if not old_sha:
        return True
    if git("cat-file", "-e", f"{old_sha}^{{commit}}", check=False).returncode:
        return True
    if git("merge-base", "--is-ancestor", old_sha, new_sha, check=False).returncode == 0:
        return True
    # old is not an ancestor: keep it only when new is strictly older (an ancestor of old); an unrelated commit (rewritten history) moves latest
    return git("merge-base", "--is-ancestor", new_sha, old_sha, check=False).returncode != 0


def latest_blocker(built, no_android):
    """T403: why `latest` must not move to a release holding `built`, or None. The owner needs an APK from `latest` at all times, so a release without
    client-android never becomes latest unless the operator passed --no-android (a deliberate APK-less release; --cli-only/--linux-only alone are not enough)."""
    if no_android or "client-android" in built:
        return None
    return "it has no client-android (the APK build failed or was skipped); pass --no-android to release without one, or fix the cause and re-run with --force"


def point_latest(root, name):
    tmp = root / ".latest.tmp"
    tmp.unlink(missing_ok=True)
    tmp.symlink_to(name)
    os.replace(tmp, root / "latest")


def main(argv):
    if not argv or argv[0].startswith("-"):
        die("usage: release.py T### [--cli-only | --client-only] [--linux-only] [--no-android] [--keep N] [--force] [--dry-run] [--keep-tmp]", 2)
    tid, opts = argv[0], argv[1:]
    cli = "--client-only" not in opts
    client = "--cli-only" not in opts
    platforms = ["linux"] if "--linux-only" in opts else CLIENT_PLATFORMS
    android = client and "--linux-only" not in opts and "--no-android" not in opts
    force, dry = "--force" in opts, "--dry-run" in opts
    keep = int(opts[opts.index("--keep") + 1]) if "--keep" in opts else KEEP_DEFAULT
    sha = git("rev-parse", "HEAD").stdout.strip()
    tree = git("rev-parse", "HEAD^{tree}").stdout.strip()
    name = f"{tid}-{sha[:9]}"
    root = releases_root()
    print(f"release: {tid} @ {sha[:9]} -> {root}/{name}")
    if not dry and git("status", "--porcelain").stdout.strip():
        die("the worktree has uncommitted changes; a release must be built from a committed tree")
    if dry:
        print(f"release: dry run (cli={cli}, client={client}, platforms={platforms + ([ANDROID_PLATFORM] if android else [])}, keep={keep})")
        return
    root.mkdir(parents=True, exist_ok=True)
    if not force:
        for d in versions(root):
            try:
                prior = json.loads((d / "manifest.json").read_text())
                if prior.get("tree") == tree and latest_blocker(prior.get("contents", []), "--no-android" in opts) is None:  # an APK-less build of the tree is retried
                    print(f"release: tree {tree[:9]} already released as {d.name}; nothing to build")
                    return
            except ValueError:
                pass
    stage = root / f".staging-{os.getpid()}"
    shutil.rmtree(stage, ignore_errors=True)
    stage.mkdir()
    built = []
    kept = False
    try:
        if cli:
            out = stage / "cli"
            run("publish Unciv.Cli", in_container(f"cd {shlex.quote(str(CSHARP))} && {DOTNET} publish Unciv.Cli -c Release -r {RID} --self-contained true -o {shlex.quote(str(out))}"))
            shutil.copytree(CSHARP.parent / "android" / "assets" / "jsons", out / "data" / "jsons")
            smoke_cli(out)
            built.append("cli")
            for rid in SERVER_RIDS:
                out = stage / f"server-{rid}"
                try:
                    run(f"publish Unciv.Server ({rid})", in_container(f"cd {shlex.quote(str(CSHARP))} && {DOTNET} publish Unciv.Server -c Release -r {rid} --self-contained true -o {shlex.quote(str(out))}"))
                    shutil.copytree(CSHARP.parent / "android" / "assets" / "jsons", out / "data" / "jsons")
                    shutil.copy2(CSHARP / "Unciv.Server" / "README.md", out / "README.md")
                    if rid == "linux-x64":
                        smoke_server(out)
                except SystemExit:
                    if rid == "linux-x64":
                        raise  # the Linux server is the one we run: its failure fails the release
                    shutil.rmtree(out, ignore_errors=True)
                    print(f"release: WARNING {rid} server failed and is left out of this release", file=sys.stderr)
                    continue
                built.append(f"server-{rid}")
            for rid in RELAY_RIDS:
                out = stage / f"relay-{rid}"
                try:
                    run(f"publish Unciv.Relay ({rid})", in_container(f"cd {shlex.quote(str(CSHARP))} && {DOTNET} publish Unciv.Relay -c Release -r {rid} --self-contained true -o {shlex.quote(str(out))}"))
                    for rel in ("README.md", "SETUP.md"):
                        shutil.copy2(CSHARP / "Unciv.Relay" / rel, out / rel)
                    shutil.copytree(CSHARP / "Unciv.Relay" / "deploy", out / "deploy", ignore=lambda d, names: [n for n in names if n == ".env" or (Path(d).name == "certs" and n != ".gitkeep")])  # never a local .env or certificate
                    if rid == "linux-x64":
                        smoke_relay(out)
                except SystemExit:
                    if rid == "linux-x64":
                        raise  # the Linux relay is the one we run: its failure fails the release
                    shutil.rmtree(out, ignore_errors=True)
                    print(f"release: WARNING {rid} relay failed and is left out of this release", file=sys.stderr)
                    continue
                built.append(f"relay-{rid}")
        if client:
            for platform in platforms:
                try:
                    run(f"package client ({platform})", in_container(f"cd {shlex.quote(str(CSHARP))} && Unciv.Godot/tools/package.sh {platform}"), crash_retries=CRASH_RETRIES)
                    run(f"check {platform} archive", f"python3 -I {shlex.quote(str(HERE / 'tools' / 'check_package.py'))} {platform}")
                except SystemExit:
                    if platform == "linux":
                        raise  # the Linux client is the only one we run: its failure fails the release
                    print(f"release: WARNING {platform} client failed and is left out of this release (linux and the CLI are unaffected)", file=sys.stderr)
                    continue
                shutil.copytree(CSHARP / "Unciv.Godot" / "build" / platform, stage / f"client-{platform}", symlinks=True)
                built.append(f"client-{platform}")
            if android and build_android(stage):
                built.append("client-android")
        manifest = {"ticket": tid, "commit": sha, "tree": tree, "built": int(time.time()), "contents": built}
        (stage / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
        with open(root / ".lock", "w") as lock:
            fcntl.flock(lock, fcntl.LOCK_EX)  # one publisher at a time; builds above ran unlocked in this worktree
            dest = root / name
            blocker = latest_blocker(built, "--no-android" in opts)
            if blocker and has_apk(dest):  # T403: --force / a retry must never replace a good release (with an APK) by a build without one
                kept = True
                moved = False
            else:
                aside = root / f".old-{os.getpid()}"
                shutil.rmtree(aside, ignore_errors=True)
                if dest.exists():
                    os.replace(dest, aside)  # move aside first so a crash cannot leave `latest` dangling with no folder
                os.replace(stage, dest)
                shutil.rmtree(aside, ignore_errors=True)
                old = None
                if (root / "latest").exists():
                    try:
                        old = json.loads((root / "latest" / "manifest.json").read_text()).get("commit")
                    except ValueError:
                        old = None
                moved = blocker is None and is_newer(sha, old)
            if moved:
                point_latest(root, name)
            gone = prune(root, keep)
            with open(root / "releases.log", "a") as log:
                note = "" if moved else " (existing release with an APK kept: this build has no client-android)" if kept else " (latest kept: " + ("no client-android" if blocker else "newer commit already released") + ")"
                log.write(f"{time.strftime('%Y-%m-%d %H:%M:%S')} {tid} {sha[:9]} {','.join(built)}{note}\n")
    finally:
        shutil.rmtree(stage, ignore_errors=True)
    if kept:
        print(f"release: {dest} already holds an APK and this build has none: left untouched")
    else:
        print(f"release: published {dest}" + (" and moved latest" if moved else " (latest not moved: " + (blocker or "a newer commit is already there") + ")"))
    if gone:
        print(f"release: pruned {', '.join(gone)}")
    if blocker:
        die(f"latest not moved: {blocker}")


def run_with_tmp(argv):
    """T390: the build's temp files land in a per-run folder that is removed on exit (--keep-tmp / UNCIV_KEEP_TMP=1 keeps it)."""
    sys.path.insert(0, str(Path(__file__).resolve().parent))
    import runtmp
    argv = [a for a in argv if a != "--keep-tmp"]
    keep = runtmp.keep_requested() or "--keep-tmp" in sys.argv
    global DOTNET
    DOTNET = DOTNET.replace("env ", "env TMPDIR=" + shlex.quote(runtmp.acquire()) + " ", 1)  # reaches the container too
    try:
        main(argv)
    finally:
        runtmp.release(keep)


if __name__ == "__main__":
    run_with_tmp(sys.argv[1:])
