package storetest

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func TestMemoryStoresAreDistinctAndPersistWhileOpen(t *testing.T) {
	t.Parallel()
	a, b := Open(t), Open(t)
	ctx := context.Background()
	ia, err := a.Identity(ctx)
	if err != nil {
		t.Fatal(err)
	}
	ib, err := b.Identity(ctx)
	if err != nil || ia == ib {
		t.Fatal("in-memory stores share identity", ia, ib, err)
	}
	// The single pooled connection keeps the database alive across calls.
	again, err := a.Identity(ctx)
	if err != nil || again != ia {
		t.Fatal("identity lost between calls", again, ia, err)
	}
}

func TestMemoryURIMustBeSharedCacheMemory(t *testing.T) {
	t.Parallel()
	if _, err := store.Open(context.Background(), store.MemoryPrefix+"x?mode=memory"); err == nil {
		t.Fatal("private-cache memory database accepted")
	}
}

func TestMemoryStoreSurvivesCloseAndReopen(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	uri := Path(t)
	first, err := store.Open(ctx, uri)
	if err != nil {
		t.Fatal(err)
	}
	id, err := first.Identity(ctx)
	if err != nil {
		t.Fatal(err)
	}
	if err = first.Close(); err != nil {
		t.Fatal(err)
	}
	second, err := store.Open(ctx, uri)
	if err != nil {
		t.Fatal(err)
	}
	defer second.Close()
	if again, err := second.Identity(ctx); err != nil || again != id {
		t.Fatal("reopen lost the database", again, id, err)
	}
}
