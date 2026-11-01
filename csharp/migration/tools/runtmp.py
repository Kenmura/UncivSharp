"""Per-run scratch directory for the tools (T390).

dotnet (MSBuild), the test hosts and the harness scenarios all create files and folders under $TMPDIR (default /tmp) and many
never remove them: before T390 /tmp held ~95,000 leftover entries (65,000 empty MSBuildTemp* folders, 28,000 empty 6-character
folders, thousands of unciv-* test folders). gate.py, ptest.py and release.py therefore call acquire() on start: it creates
/tmp/unciv-run-<pid>-<random>, exports it as TMPDIR (and UNCIV_RUN_TMP, so child tools reuse it instead of nesting a second one)
and release() removes it on exit, also after a failure. Keep it with UNCIV_KEEP_TMP=1 or --keep-tmp (the path is printed).
tools/reap.py --scratch lists/removes folders of runs that died without cleaning up (the pid is part of the name).
"""
import os
import re
import shutil
import sys
import tempfile

PREFIX = "unciv-run-"
ENV_DIR = "UNCIV_RUN_TMP"
ENV_KEEP = "UNCIV_KEEP_TMP"

MIN_FREE_BYTES = int(float(os.environ.get("UNCIV_MIN_TMP_FREE_GB", "2")) * 1024 ** 3)  # a full gate peaks well under this; below it the run dies with dozens of opaque IO failures (T402). Override: UNCIV_MIN_TMP_FREE_GB
MIN_FREE_INODES = 5000
QUOTA_RE = re.compile(r"Disk quota exceeded|No space left on device|ENOSPC|EDQUOT|There is not enough space on the disk")

_owned = None
_saved = None  # (TMPDIR, tempfile.tempdir) from before acquire()


def force_rmtree(path):
    """shutil.rmtree that also removes read-only folders (a test leaves dr-x folders behind on purpose, which plain rmtree cannot enter)."""
    top = os.path.abspath(path)

    def fix(func, p, _exc):
        try:
            for q in (os.path.dirname(p), p):
                # never chmod through a symlink, and never above the tree being removed
                if (q == top or q.startswith(top + os.sep)) and not os.path.islink(q):
                    os.chmod(q, 0o700)
            func(p)
        except OSError:
            pass
    for _ in range(2):  # the second pass handles folders that only became removable after their parent was fixed
        if sys.version_info >= (3, 12):
            shutil.rmtree(path, onexc=fix)
        else:
            shutil.rmtree(path, onerror=fix)
        if not os.path.exists(path):
            return


def keep_requested(argv=None):
    return os.environ.get(ENV_KEEP, "") not in ("", "0") or "--keep-tmp" in (sys.argv if argv is None else argv)


def acquire():
    """Create (or reuse the parent run's) scratch dir and point TMPDIR at it. Returns its path."""
    global _owned, _saved
    inherited = os.environ.get(ENV_DIR)
    if inherited and os.path.isdir(inherited):
        return inherited
    _saved = (os.environ.get("TMPDIR"), tempfile.tempdir)
    path = tempfile.mkdtemp(prefix="%s%d-" % (PREFIX, os.getpid()))
    _owned = path
    os.environ[ENV_DIR] = path
    os.environ["TMPDIR"] = path
    tempfile.tempdir = path
    return path


def release(keep=False):
    """Remove the dir this process created (never an inherited one). Best effort, never raises."""
    global _owned
    path, _owned = _owned, None
    if not path:
        return
    old_tmpdir, tempfile.tempdir = _saved
    os.environ.pop(ENV_DIR, None)
    if old_tmpdir is None:
        os.environ.pop("TMPDIR", None)
    else:
        os.environ["TMPDIR"] = old_tmpdir
    if keep or keep_requested():
        print("tmp: kept %s" % path, file=sys.stderr)
        return
    force_rmtree(path)


def scratch_stats(path=None):
    """-> (free bytes, free inodes or None, mount directory) of the filesystem holding the scratch dir."""
    path = path or tempfile.gettempdir()
    st = os.statvfs(path)
    return st.f_bavail * st.f_frsize, (st.f_favail if st.f_files else None), path


def exhaustion_message(path=None, free_bytes=None, free_inodes=None):
    """-> one clear message when the scratch filesystem is (nearly) full, else None. Numbers are injectable for tests."""
    if free_bytes is None:
        try:
            free_bytes, free_inodes, path = scratch_stats(path)
        except OSError:
            return None
    path = path or tempfile.gettempdir()
    if os.path.basename(path).startswith(PREFIX):
        path = os.path.dirname(path)  # name the shared folder, not this run's empty one
    if free_bytes >= MIN_FREE_BYTES and (free_inodes is None or free_inodes >= MIN_FREE_INODES):
        return None
    entries = 0
    try:
        entries = sum(1 for _ in os.scandir(path))
    except OSError:
        pass
    return ("TMP EXHAUSTED: %s has %.1f GiB and %s inodes free (the gate needs %.0f GiB). Nothing was run: the tests would fail with "
            "'Disk quota exceeded' in dozens of unrelated places. %d entries sit directly in it. Free space first: "
            "`python3 -I tools/reap.py --scratch` lists the stale scratch folders (largest first; add --remove to delete only the STALE ones), "
            "or point TMPDIR at a bigger disk." % (path, free_bytes / 1024 ** 3, "?" if free_inodes is None else free_inodes, MIN_FREE_BYTES / 1024 ** 3, entries))


def mentions_exhaustion(text):
    return bool(QUOTA_RE.search(text or ""))


def require_space():
    """Call right after acquire(): prints the exhaustion message to stderr and returns False when the run cannot succeed."""
    msg = exhaustion_message()
    if msg:
        print("tmp: " + msg, file=sys.stderr)
        return False
    return True
