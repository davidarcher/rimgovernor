package store

import (
	"context"
	"os"
)

// SnapshotReplace writes a Snapshot of the whole store beside path and renames
// it over path, so a reader opening path sees the previous snapshot or the new
// one and never a partial file. The copy is derived and read-only: nothing in
// the controller reads it back.
func (s *Store) SnapshotReplace(ctx context.Context, path string) error {
	tmp := path + ".tmp"
	for _, suffix := range []string{"", "-wal", "-shm"} {
		if err := os.Remove(tmp + suffix); err != nil && !os.IsNotExist(err) {
			return err
		}
	}
	if err := s.Snapshot(ctx, tmp); err != nil {
		_ = os.Remove(tmp)
		return err
	}
	return os.Rename(tmp, path)
}
