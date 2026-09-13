package store

import (
	"context"
	"os"
	"path/filepath"
	"sync"
	"testing"
	"time"
)

func TestIdentityPersistentAndDatabaseScoped(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, path := fixture(t)
	id, err := s.Identity(ctx)
	if err != nil || len(id) != 64 {
		t.Fatal(id, err)
	}
	if err = s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	again, err := s.Identity(ctx)
	if err != nil || again != id {
		t.Fatal("restart changed identity", again, err)
	}
	other, _ := fixture(t)
	different, err := other.Identity(ctx)
	if err != nil || different == id {
		t.Fatal("databases share identity", err)
	}
	if err = s.Close(); err != nil {
		t.Fatal(err)
	}
	if _, err = s.Identity(ctx); err == nil {
		t.Fatal("closed store returned identity")
	}
}
func TestConcurrentInitializationSharesIdentity(t *testing.T) {
	t.Parallel()
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	path := filepath.Join(t.TempDir(), "concurrent.db")
	start := make(chan struct{})
	ids := make(chan ControllerSessionID, 8)
	errs := make(chan error, 8)
	var wg sync.WaitGroup
	for range 8 {
		wg.Add(1)
		go func() {
			defer wg.Done()
			<-start
			s, err := Open(ctx, path)
			if err != nil {
				errs <- err
				return
			}
			defer s.Close()
			id, err := s.Identity(ctx)
			if err != nil {
				errs <- err
				return
			}
			ids <- id
		}()
	}
	close(start)
	wg.Wait()
	close(ids)
	close(errs)
	for err := range errs {
		t.Fatal(err)
	}
	var first ControllerSessionID
	count := 0
	for id := range ids {
		count++
		if first == "" {
			first = id
		}
		if id != first {
			t.Fatal("concurrent open generated different identity")
		}
	}
	if count != 8 {
		t.Fatal("missing concurrent results", count)
	}
}
func TestIdentityCorruptionFailsWithoutRepair(t *testing.T) {
	t.Parallel()
	for _, sql := range []string{
		"DELETE FROM metadata", "UPDATE metadata SET controller_session_id='broken'", "UPDATE metadata SET controller_session_id=upper(controller_session_id)",
		"DROP TABLE metadata", "PRAGMA user_version=1",
		"ALTER TABLE metadata RENAME TO old_metadata; CREATE TABLE metadata(singleton INTEGER,controller_session_id TEXT); INSERT INTO metadata SELECT * FROM old_metadata; INSERT INTO metadata SELECT * FROM old_metadata",
	} {
		t.Run(sql, func(t *testing.T) {
			s, path := fixture(t)
			if _, err := s.db.Exec(sql); err != nil {
				t.Fatal(err)
			}
			if err := s.Close(); err != nil {
				t.Fatal(err)
			}
			if reopened, err := Open(context.Background(), path); err == nil {
				reopened.Close()
				t.Fatal("corrupt identity accepted")
			}
			// Failed Open must close the handle, including on Windows.
			if err := os.Rename(path, path+".rejected"); err != nil {
				t.Fatal(err)
			}
		})
	}
}
func TestCancelledIdentityInitializationCanReopen(t *testing.T) {
	t.Parallel()
	path := filepath.Join(t.TempDir(), "cancelled.db")
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	if s, err := Open(ctx, path); err == nil {
		s.Close()
		t.Fatal("cancelled initialization succeeded")
	}
	s := open(t, path)
	id, err := s.Identity(context.Background())
	if err != nil || id == "" {
		t.Fatal(id, err)
	}
	if _, err = s.Identity(ctx); err == nil {
		t.Fatal("cancelled identity read succeeded")
	}
}
