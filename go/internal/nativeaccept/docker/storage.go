package docker

import (
	"bytes"
	"context"
	"database/sql"
	"fmt"
	"io/fs"
	"os/exec"
	"path/filepath"
	"strings"
	"time"

	_ "modernc.org/sqlite"
)

// Storage selects where a worker's writable /worker tree lives.
//
// StorageVolume (the default) puts it on a private Docker volume: the
// controller's SQLite state database and the flight recorder then fsync
// against the engine's own Linux filesystem, not through a Docker Desktop
// host bind mount whose synchronous-write path is both slow and a source of
// spurious "database is locked"/short-write failures. The tree is exported
// to the host root only after the container has stopped (never a live
// SQLite database and its changing WAL independently), every exported
// database must pass PRAGMA integrity_check, and the volume is removed only
// once export succeeded and the container is gone -- otherwise it is
// retained as recovery evidence and named in the StorageEvidence.
//
// StorageBind mounts the host root directly, the explicit comparison option:
// same evidence layout, live-inspectable while running, but every
// controller write crosses the bind mount.
type Storage string

const (
	StorageVolume Storage = "volume"
	StorageBind   Storage = "bind"
)

// ParseStorage accepts the -storage flag spellings; "" means StorageVolume.
func ParseStorage(value string) (Storage, error) {
	switch Storage(value) {
	case "", StorageVolume:
		return StorageVolume, nil
	case StorageBind:
		return StorageBind, nil
	}
	return "", fmt.Errorf("unknown worker storage %q (want volume or bind)", value)
}

// StorageEvidence is what Worker.Stop records about the storage contract, in
// a shape that drops straight into a nativeaccept.Report.
type StorageEvidence struct {
	Kind   Storage `json:"kind"`
	Volume string  `json:"volume,omitempty"`
	// Stopped is true once `docker stop` (or an observed exit) confirmed the
	// container was no longer running before any export was attempted.
	Stopped bool `json:"stopped"`
	// Exported is true once every worker-owned entry (see exportEntries)
	// was copied to the host root and every database passed its integrity
	// check. Always true for StorageBind, which never needs an export.
	Exported    bool     `json:"exported"`
	ExportError string   `json:"export_error,omitempty"`
	Databases   []string `json:"databases,omitempty"`
	// Retained is true while the private volume still exists: export failed,
	// container removal failed, or the volume removal itself failed. A
	// retained volume is recovery evidence, never a leak to be silently swept.
	Retained     bool   `json:"retained"`
	CleanupError string `json:"cleanup_error,omitempty"`
}

// exportEntries are the /worker entries a stopped volume-backed worker
// exports to the host root. It is a fixed list rather than `/worker/.`
// because /worker/game is populated by read-only per-entry bind mounts of
// the licensed game install (see gameEntryMounts), which docker cp would
// happily copy in full. SQLite's -wal/-shm sidecars normally vanish on a
// clean close but are exported when a crash left them behind, so the
// exported database still opens with its un-checkpointed pages.
var exportEntries = []string{
	"state.db", "state.db-wal", "state.db-shm",
	"flight.jsonl", "HeadlessPlayer.log", "profile", "config",
}

func volumeName(container string) string { return container + "-state" }

// createVolume creates the worker's private volume, labeled so one left by
// an interrupted run can be found (`docker volume ls --filter
// label=rimgovernor.worker`).
func createVolume(ctx context.Context, docker, container string) (string, error) {
	name := volumeName(container)
	cmd := exec.CommandContext(ctx, docker, "volume", "create", "--label", "rimgovernor.worker="+container, name)
	if out, err := cmd.CombinedOutput(); err != nil {
		return "", fmt.Errorf("docker volume create %s: %w: %s", name, err, out)
	}
	return name, nil
}

// seedVolume copies the host-prepared config and profile trees into the
// created (not yet started) container, which lands them on the volume
// mounted at /worker.
func seedVolume(ctx context.Context, docker, container, root string) error {
	for _, entry := range []string{"config", "profile"} {
		cmd := exec.CommandContext(ctx, docker, "cp", filepath.Join(root, entry), container+":/worker/")
		if out, err := cmd.CombinedOutput(); err != nil {
			return fmt.Errorf("docker cp %s into %s: %w: %s", entry, container, err, out)
		}
	}
	return nil
}

// stopContainer asks the container to exit within grace, so rimgovernor
// closes its database cleanly, and confirms it is no longer running.
func stopContainer(ctx context.Context, docker, container string, grace time.Duration) error {
	running, err := ContainerState(ctx, docker, container)
	if err != nil {
		return err
	}
	if running {
		cmd := exec.CommandContext(ctx, docker, "stop", "--time", fmt.Sprint(int(grace.Seconds())), container)
		if out, err := cmd.CombinedOutput(); err != nil {
			return fmt.Errorf("docker stop %s: %w: %s", container, err, out)
		}
	}
	if running, err = ContainerState(ctx, docker, container); err != nil {
		return err
	} else if running {
		return fmt.Errorf("container %s still running after docker stop", container)
	}
	return nil
}

// exportWorker copies exportEntries from the stopped container's /worker to
// root and integrity-checks every exported SQLite database. Missing optional
// entries (a checkpointed -wal, a run that never wrote a log) are skipped;
// state.db itself must exist.
func exportWorker(ctx context.Context, docker, container, root string) ([]string, error) {
	for _, entry := range exportEntries {
		cmd := exec.CommandContext(ctx, docker, "cp", container+":/worker/"+entry, root)
		var out bytes.Buffer
		cmd.Stdout, cmd.Stderr = &out, &out
		if err := cmd.Run(); err != nil {
			if entry != "state.db" && strings.Contains(out.String(), "Could not find the file") {
				continue
			}
			return nil, fmt.Errorf("docker cp %s:/worker/%s: %w: %s", container, entry, err, out.String())
		}
	}
	var databases []string
	err := filepath.WalkDir(root, func(path string, d fs.DirEntry, err error) error {
		if err != nil || d.IsDir() {
			return err
		}
		if ext := filepath.Ext(path); ext != ".db" && ext != ".sqlite" {
			return nil
		}
		if err := checkIntegrity(path); err != nil {
			return err
		}
		relative, _ := filepath.Rel(root, path)
		databases = append(databases, filepath.ToSlash(relative))
		return nil
	})
	return databases, err
}

func checkIntegrity(path string) error {
	db, err := sql.Open("sqlite", "file:"+filepath.ToSlash(path)+"?mode=ro")
	if err != nil {
		return fmt.Errorf("open exported database %s: %w", path, err)
	}
	defer db.Close()
	var result string
	if err := db.QueryRow("PRAGMA integrity_check").Scan(&result); err != nil {
		return fmt.Errorf("integrity_check %s: %w", path, err)
	}
	if result != "ok" {
		return fmt.Errorf("exported database %s failed integrity_check: %s", path, result)
	}
	return nil
}

// removeVolume removes the volume and verifies it is gone, so "cleaned up"
// in the evidence means an inspect afterwards found nothing.
func removeVolume(ctx context.Context, docker, name string) error {
	cmd := exec.CommandContext(ctx, docker, "volume", "rm", name)
	if out, err := cmd.CombinedOutput(); err != nil {
		return fmt.Errorf("docker volume rm %s: %w: %s", name, err, out)
	}
	if exec.CommandContext(ctx, docker, "volume", "inspect", name).Run() == nil {
		return fmt.Errorf("volume %s still exists after docker volume rm", name)
	}
	return nil
}
