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
# What this script does do: if GABS_BIN/GAME_ID/GABS_CONFIG_DIR are set, runs
# `gabs games start` once before exec'ing rimgovernor. GABS enforces
# single-attachment ownership per game over its MCP/GABP session (see
# go/internal/nativeaccept/docker's package comment -- this is why that package
# never opens its own bridge.Client alongside a container's rimgovernor
# process); rimgovernor's own `serve --gabs ...` only ever calls
# games_connect, never games_start, so something has to launch the game
# first. The `gabs games start` CLI form is exactly that: it launches the
# game, verifies it came up, and exits without holding any bridge connection
# open -- see the shared GABS docs' "started_attachment_deferred" status and
# its README ("the CLI verifies the launch and then exits without holding a
# bridge connection"). That leaves the runtime claim active for
# rimgovernor's own MCP session to attach to below.
#
# Finally runs ./rimgovernor with the container's CMD/`docker run` arguments
# ("$@" here is the CMD array, not including the entrypoint binary itself).
# A cold headless RimWorld boot (asset/def-database load, mod init) can take
# longer than rimgovernor's own single games_connect attempt allows (its
# --timeout is capped at 60s). The game process and GABS's runtime claim
# from games_start above stay up regardless, so retrying the whole
# rimgovernor process is a valid, if coarse, way to keep re-attempting
# games_connect until the bridge is actually ready -- no need to poll
# games_status separately first.
#
# The retry loop means rimgovernor cannot simply be exec'd, so `docker stop`'s
# SIGTERM (via --init) lands on this shell, not on rimgovernor. It is
# forwarded explicitly: rimgovernor handles SIGTERM by closing its SQLite
# state cleanly, which is what lets docker.Worker.Stop export a checkpointed
# database instead of one with a live WAL, and a forwarded stop must not be
# retried as a failed attempt.
set -e
if [ -n "$GABS_BIN" ] && [ -n "$GAME_ID" ] && [ -n "$GABS_CONFIG_DIR" ]; then
    "$GABS_BIN" games start "$GAME_ID" --configDir "$GABS_CONFIG_DIR"
fi
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
