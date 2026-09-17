package store

import (
	"context"
	"crypto/rand"
	"database/sql"
	"encoding/hex"
	"fmt"
	"sync"
	"sync/atomic"
	"testing"
)

// memoryPath is this package's copy of storetest.Path (storetest imports
// store): a unique in-memory database cloned from a template migrated once
// per binary, given its own controller identity, and kept alive by a keeper
// connection until cleanup so a test can close and reopen it by URI.
var memorySequence atomic.Uint64

var memoryTemplate struct {
	once sync.Once
	db   *sql.DB
	err  error
}

func memoryURI() string {
	return fmt.Sprintf("%smem%d?mode=memory&cache=shared", MemoryPrefix, memorySequence.Add(1))
}
func memoryKeep(uri string) (*sql.DB, error) {
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
func memoryPath(t testing.TB) string {
	t.Helper()
	memoryTemplate.once.Do(func() {
		uri := memoryURI()
		keeper, err := memoryKeep(uri)
		if err != nil {
			memoryTemplate.err = err
			return
		}
		s, err := Open(context.Background(), uri)
		if err == nil {
			err = s.Close()
		}
		if err != nil {
			_ = keeper.Close()
			memoryTemplate.err = err
			return
		}
		memoryTemplate.db = keeper
	})
	if memoryTemplate.err != nil {
		t.Fatal("memory template:", memoryTemplate.err)
	}
	uri := memoryURI()
	keeper, err := memoryKeep(uri)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = keeper.Close() })
	ctx := context.Background()
	if _, err = memoryTemplate.db.ExecContext(ctx, "VACUUM INTO '"+uri+"'"); err != nil {
		t.Fatal("memory clone:", err)
	}
	var entropy [32]byte
	if _, err = rand.Read(entropy[:]); err != nil {
		t.Fatal(err)
	}
	if _, err = keeper.ExecContext(ctx, "UPDATE metadata SET controller_session_id=?", hex.EncodeToString(entropy[:])); err != nil {
		t.Fatal("memory identity:", err)
	}
	return uri
}
