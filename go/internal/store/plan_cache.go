package store

import (
	"context"
	"fmt"
	"reflect"
	"sync"
	"sync/atomic"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"modernc.org/sqlite"
)

// planCache keeps the decoded state of each non-retired plan between reads.
//
// Decoding a plan (its action rows, dependencies, every transition replayed
// into a Progress, its admissions) is the store's dominant allocation, and the
// planners read the whole catalog many times per scheduler step. Writes touch
// one plan at a time, so each entry is dropped when a row that feeds it is
// written, not when anything is.
//
// The store cannot forget to invalidate: the cache watches the connection
// itself. A pre-update hook sees every INSERT, UPDATE and DELETE of the five
// tables load reads (plans, actions, action_dependencies, transitions,
// admissions), whichever store function issued it. Two things the hook cannot
// see are covered separately: a write by another connection or process
// changes SQLite's data_version, which flushes the cache; and a rolled-back
// transaction that wrote plan rows may have been read back by its own loads,
// so the rollback hook flushes it.
//
// The database has one connection, so reads and writes never interleave: a
// load runs only between transactions or inside the one that is writing.
type planCache struct {
	mu      sync.Mutex
	plans   map[domain.PlanID]PlanState
	owner   map[domain.ActionID]domain.PlanID
	version int64
	// wrote is set when the open transaction wrote a row that feeds a plan.
	wrote bool

	hits, misses, drops, flushes atomic.Int64
}

func newPlanCache() *planCache {
	return &planCache{plans: map[domain.PlanID]PlanState{}, owner: map[domain.ActionID]domain.PlanID{}, version: -1}
}

// PlanCacheStats are the cache's lifetime counters.
type PlanCacheStats struct{ Hits, Misses, Drops, Flushes int64 }

// PlanCacheStats reports how often a plan read was served from the cache.
func (s *Store) PlanCacheStats() PlanCacheStats {
	c := s.plans
	if c == nil {
		return PlanCacheStats{}
	}
	return PlanCacheStats{c.hits.Load(), c.misses.Load(), c.drops.Load(), c.flushes.Load()}
}

// SQLite operation codes carried by the pre-update hook.
const (
	sqliteDelete = 9
	sqliteInsert = 18
	sqliteUpdate = 23
)

// install registers the cache's hooks on a new connection.
func (c *planCache) install(conn any) {
	h, ok := conn.(sqlite.HookRegisterer)
	if !ok {
		return
	}
	h.RegisterPreUpdateHook(c.observe)
	h.RegisterCommitHook(func() int32 {
		c.mu.Lock()
		c.wrote = false
		c.mu.Unlock()
		return 0
	})
	h.RegisterRollbackHook(func() {
		c.mu.Lock()
		if c.wrote {
			c.clearLocked()
		}
		c.wrote = false
		c.mu.Unlock()
	})
}

// observe drops every cached plan that a write to d's row affects.
func (c *planCache) observe(d sqlite.SQLitePreUpdateData) {
	var column int
	switch d.TableName {
	case "plans", "action_dependencies", "admissions":
		column = 0
	case "actions":
		column = 1
	case "transitions":
		column = 1
	default:
		return
	}
	read := func(old bool) (string, bool) {
		vals := make([]any, column+1)
		var err error
		if old {
			err = d.Old(vals...)
		} else {
			err = d.New(vals...)
		}
		if err != nil {
			return "", false
		}
		switch v := vals[column].(type) {
		case string:
			return v, true
		case []byte:
			return string(v), true
		}
		return "", false
	}
	var keys []string
	if d.Op != sqliteInsert {
		if k, ok := read(true); ok {
			keys = append(keys, k)
		} else {
			c.flush()
			return
		}
	}
	if d.Op != sqliteDelete {
		if k, ok := read(false); ok {
			keys = append(keys, k)
		} else {
			c.flush()
			return
		}
	}
	c.mu.Lock()
	defer c.mu.Unlock()
	c.wrote = true
	for _, key := range keys {
		switch d.TableName {
		case "plans", "actions", "action_dependencies":
			c.dropLocked(domain.PlanID(key))
		default:
			// transitions and admissions are keyed by action; an action in no
			// cached plan has nothing to drop.
			if plan, ok := c.owner[domain.ActionID(key)]; ok {
				c.dropLocked(plan)
			}
		}
	}
}

func (c *planCache) dropLocked(id domain.PlanID) {
	state, ok := c.plans[id]
	if !ok {
		return
	}
	for _, a := range state.Spec.Actions() {
		delete(c.owner, a.ID())
	}
	delete(c.plans, id)
	c.drops.Add(1)
}

func (c *planCache) clearLocked() {
	clear(c.plans)
	clear(c.owner)
	c.flushes.Add(1)
}

func (c *planCache) flush() {
	c.mu.Lock()
	c.clearLocked()
	c.wrote = true
	c.mu.Unlock()
}

// get returns the plan, from the cache when it is held. A nil cache decodes
// from the database every time.
func (c *planCache) get(ctx context.Context, tx *Tx, id domain.PlanID) (PlanState, error) {
	if c == nil {
		return decodePlan(ctx, tx, id)
	}
	if err := c.syncVersion(ctx, tx); err != nil {
		return PlanState{}, err
	}
	c.mu.Lock()
	held, ok := c.plans[id]
	c.mu.Unlock()
	if ok {
		c.hits.Add(1)
		if testing.Testing() {
			if err := c.verify(ctx, tx, id, held); err != nil {
				return PlanState{}, err
			}
		}
		return held.detached(), nil
	}
	c.misses.Add(1)
	state, err := decodePlan(ctx, tx, id)
	if err != nil {
		return PlanState{}, err
	}
	if !state.Retired {
		c.mu.Lock()
		c.plans[id] = state
		for _, a := range state.Spec.Actions() {
			c.owner[a.ID()] = id
		}
		c.mu.Unlock()
	}
	return state.detached(), nil
}

// syncVersion flushes the cache when another connection has committed since
// the last read: SQLite's data_version changes only for commits made by other
// connections, which the hooks on this one cannot see.
func (c *planCache) syncVersion(ctx context.Context, tx *Tx) error {
	var v int64
	if err := tx.QueryRowContext(ctx, "PRAGMA data_version").Scan(&v); err != nil {
		return err
	}
	c.mu.Lock()
	if v != c.version {
		c.clearLocked()
		c.version = v
	}
	c.mu.Unlock()
	return nil
}

// verify is the test-only proof that no write path escaped the hooks: a hit
// must equal a fresh decode.
func (c *planCache) verify(ctx context.Context, tx *Tx, id domain.PlanID, held PlanState) error {
	fresh, err := decodePlan(ctx, tx, id)
	if err != nil {
		return err
	}
	if !reflect.DeepEqual(held, fresh) {
		return fmt.Errorf("plan cache stale for %s: cached state differs from the database", id)
	}
	return nil
}

// detached is the state with its own Progress and Admissions slices, so a
// caller that replaces an element cannot change what the cache serves. The
// plan spec is immutable.
func (p PlanState) detached() PlanState {
	p.Progress = append([]domain.Progress(nil), p.Progress...)
	p.Admissions = append([]ActionAdmission(nil), p.Admissions...)
	return p
}
