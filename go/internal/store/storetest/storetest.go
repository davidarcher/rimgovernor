// Package storetest hands tests an in-memory store. A file-backed store
// costs file creation, WAL/shm files and lock syscalls per fixture, which
// dominates fixture setup on Windows and is what balloons when several test
// suites compete for the disk; and store.Open on an empty database parses
// the whole schema, a third of the CPU a package like buildingruntime burns.
// The schema is migrated once per test binary and every fixture is a page
// copy of that template. Tests that write the file themselves or compare two
// databases keep using a real file through store.Open.
package storetest

import (
	"context"
	"crypto/rand"
	"database/sql"
	"encoding/hex"
	"fmt"
	"sync"
	"sync/atomic"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/store"
	_ "modernc.org/sqlite"
)

var sequence atomic.Uint64

var template struct {
	once sync.Once
	db   *sql.DB // keeps the migrated template alive for the binary
	err  error
}

func memoryURI() string {
	return fmt.Sprintf("%smem%d?mode=memory&cache=shared", store.MemoryPrefix, sequence.Add(1))
}

// keep opens a connection that holds a shared-cache database alive until it
// is closed. Pinging attaches it; database/sql opens lazily.
func keep(uri string) (*sql.DB, error) {
	db, err := sql.Open("sqlite", uri)
	if err != nil {
		return nil, err
	}
	db.SetMaxOpenConns(1)
	db.SetMaxIdleConns(1)
	if err = db.PingContext(context.Background()); err != nil {
		_ = db.Close()
		return nil, err
	}
	return db, nil
}

func buildTemplate() {
	uri := memoryURI()
	keeper, err := keep(uri)
	if err != nil {
		template.err = err
		return
	}
	s, err := store.Open(context.Background(), uri)
	if err == nil {
		err = s.Close()
	}
	if err != nil {
		_ = keeper.Close()
		template.err = err
		return
	}
	template.db = keeper
}

// Path returns a unique in-memory database URI for store.Open, already
// migrated and with its own controller identity. A shared-cache database
// lives while any connection is open, so a keeper connection held until
// cleanup lets a test close its store and reopen the same URI, as the
// restart tests do with a file.
func Path(t testing.TB) string {
	t.Helper()
	template.once.Do(buildTemplate)
	if template.err != nil {
		t.Fatal("storetest template:", template.err)
	}
	uri := memoryURI()
	keeper, err := keep(uri)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = keeper.Close() })
	ctx := context.Background()
	// VACUUM INTO copies the template's pages into the empty database the
	// keeper holds; the schema is not parsed again.
	if _, err = template.db.ExecContext(ctx, "VACUUM INTO '"+uri+"'"); err != nil {
		t.Fatal("storetest clone:", err)
	}
	// Migration minted the template's controller identity; each copy gets
	// its own, since two fixtures must never share one.
	var entropy [32]byte
	if _, err = rand.Read(entropy[:]); err != nil {
		t.Fatal(err)
	}
	if _, err = keeper.ExecContext(ctx, "UPDATE metadata SET controller_session_id=?", hex.EncodeToString(entropy[:])); err != nil {
		t.Fatal("storetest identity:", err)
	}
	return uri
}

// Open returns a fresh in-memory store, closed on cleanup.
func Open(t testing.TB) *store.Store {
	t.Helper()
	s, err := store.Open(context.Background(), Path(t))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = s.Close() })
	return s
}
