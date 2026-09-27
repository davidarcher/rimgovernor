#!/bin/sh
# /worker/game is populated entirely by docker.StartWorker's own `-v`
# mounts (one read-only bind per game top-level entry, plus one for Mods --
# see go/internal/nativeaccept/docker/worker.go), not by this script: RimWorld's
# Unity engine resolves its install directory from the *real* path of its
# running executable, so a merge built from symlinks into a separately
# read-only /inputs/game (an earlier version of this script) doesn't work --
# Unity follows the symlink straight back to /inputs/game, where no Mods
# directory exists. Real per-entry bind mounts under the writable /worker
# parent give it the same tree without that indirection, and without
# mutating the shared read-only input store.
#
# What this script does do: runs ./rimgovernor with the container's CMD/`docker run` arguments
# ("$@" here is the CMD array, not including the entrypoint binary itself).
# rimgovernor launches the game itself (go/internal/gamehost) and talks
# GABP to RimBridgeServer directly. A cold headless RimWorld boot
# (asset/def-database load, mod init) can outlast one connect attempt, so
# retrying the whole rimgovernor process is a valid, if coarse, way to keep
# re-attempting until the bridge is actually ready.
#
# The retry loop means rimgovernor cannot simply be exec'd, so `docker stop`'s
# SIGTERM (via --init) lands on this shell, not on rimgovernor. It is
# forwarded explicitly: rimgovernor handles SIGTERM by closing its SQLite
# state cleanly, which is what lets docker.Worker.Stop export a checkpointed
# database instead of one with a live WAL, and a forwarded stop must not be
# retried as a failed attempt.
set -e
stopping=""
child=""
forward() {
    stopping=1
    if [ -n "$child" ]; then
        kill -TERM "$child" 2>/dev/null || true
    fi
}
trap forward TERM INT
attempt=1
max_attempts=8
while [ "$attempt" -le "$max_attempts" ]; do
    ./rimgovernor "$@" &
    child=$!
    status=0
    wait "$child" || status=$?
    # A signal interrupts the first wait; wait again for the child's real exit.
    if [ -n "$stopping" ]; then
        wait "$child" 2>/dev/null || true
        exit 0
    fi
    if [ "$status" -eq 0 ]; then
        exit 0
    fi
    echo "rimgovernor attempt $attempt/$max_attempts exited $status; retrying" >&2
    attempt=$((attempt + 1))
    sleep 15
    if [ -n "$stopping" ]; then
        exit 0
    fi
done
exit "$status"
