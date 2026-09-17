package bridge

import (
	"context"
	"strings"
	"sync"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
)

// StepReadCache memoizes reviewed native observation reads for the duration of
// one scheduler step. Every planner composed into a step re-reads the same
// identity, colony facts, buildings and rooms under the same paused tick;
// with the cache attached to the step's context (WithStepReadCache) the
// first read of each (method, request) pair goes to native and the rest are
// served locally, including callers that arrive while the first is still in
// flight.
//
// Cached rows belong to one observation scope: the (load token, tick, native
// generation) the native reply reported. A fresh reply from a different
// scope, or any write issued through a context carrying the cache, discards
// every row, so a planner never sees a fact from before a write or from an
// earlier tick. Only replies carrying an ObservationContext are cached;
// refusals, unavailability and typed failures are never memoized. A hit is
// decoded into a fresh reply message, so the typed adapters validate it
// exactly as they validate a native reply.
//
// The cache is meant to live for one step and be dropped; it never spans
// steps or ticks.
type StepReadCache struct {
	mu      sync.Mutex
	scope   readScope
	epoch   uint64
	entries map[readCacheKey]*readCacheEntry
	stats   StepReadCacheStats
}

// StepReadCacheStats counts a cache's outcomes. Hits were served locally;
// Misses went to native (whether or not the reply was then cached);
// Coalesced hits waited on an identical read already in flight; Invalidations
// are the times the rows were discarded because a reply reported a new
// observation scope or a write went through the cached context.
type StepReadCacheStats struct {
	Hits, Misses, Coalesced, Invalidations uint64
}

type readScope struct {
	load       string
	tick       int64
	generation uint64
}

type readCacheKey struct {
	method  string
	request string
}

type readCacheEntry struct {
	done    chan struct{}
	epoch   uint64
	payload []byte
	result  Result
	ok      bool
}

type stepReadCacheKey struct{}

// NewStepReadCache returns an empty cache for one step.
func NewStepReadCache() *StepReadCache {
	return &StepReadCache{entries: map[readCacheKey]*readCacheEntry{}}
}

// WithStepReadCache attaches cache to ctx so Client's reviewed reads consult
// it. Contexts derived from ctx carry it too, which is how a step's parallel
// planners share one cache.
func WithStepReadCache(ctx context.Context, cache *StepReadCache) context.Context {
	if cache == nil {
		return ctx
	}
	return context.WithValue(ctx, stepReadCacheKey{}, cache)
}

// StepReadCacheFrom returns the cache ctx carries, or nil.
func StepReadCacheFrom(ctx context.Context) *StepReadCache {
	cache, _ := ctx.Value(stepReadCacheKey{}).(*StepReadCache)
	return cache
}

// Stats returns the counts so far.
func (s *StepReadCache) Stats() StepReadCacheStats {
	if s == nil {
		return StepReadCacheStats{}
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.stats
}

// Invalidate discards every row. Reads already in flight will not be stored.
func (s *StepReadCache) Invalidate() {
	if s == nil {
		return
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	s.invalidateLocked()
}

func (s *StepReadCache) invalidateLocked() {
	s.epoch++
	s.scope = readScope{}
	s.entries = map[readCacheKey]*readCacheEntry{}
	s.stats.Invalidations++
}

// cacheableRead reports whether a reviewed read is a pure observation whose
// reply carries an ObservationContext. Clock, authority, presentation,
// receipt and preview reads are excluded: they report live controller or UI
// state rather than paused-world facts, or have side effects.
func cacheableRead(name string) bool {
	return name == "rimgovernor/lifecycle_read_identity" || strings.HasPrefix(name, "rimgovernor/observations_")
}

// acquire returns the entry for key and whether the caller is its leader:
// the leader performs the native read and completes the entry; followers
// wait on it.
func (s *StepReadCache) acquire(key readCacheKey) (*readCacheEntry, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if entry := s.entries[key]; entry != nil {
		return entry, false
	}
	entry := &readCacheEntry{done: make(chan struct{}), epoch: s.epoch}
	s.entries[key] = entry
	return entry, true
}

// complete finishes a leader's entry. A reply with a scope is stored unless
// the cache was invalidated since acquire; a different scope discards the
// older rows first. Without a reply (error, refusal, no context) the entry
// is dropped so followers read natively themselves.
func (s *StepReadCache) complete(key readCacheKey, entry *readCacheEntry, scope *readScope, payload []byte, result Result) {
	s.mu.Lock()
	defer s.mu.Unlock()
	defer close(entry.done)
	s.stats.Misses++
	if scope == nil || entry.epoch != s.epoch {
		if s.entries[key] == entry {
			delete(s.entries, key)
		}
		return
	}
	if s.scope != *scope {
		if s.scope != (readScope{}) {
			s.invalidateLocked()
		}
		s.scope = *scope
		entry.epoch = s.epoch
		s.entries[key] = entry
	}
	entry.payload, entry.result, entry.ok = payload, result, true
}

// wait blocks a follower until the leader completes or ctx ends. It returns
// the payload only when the leader stored one.
func (s *StepReadCache) wait(ctx context.Context, entry *readCacheEntry) ([]byte, Result, bool, error) {
	select {
	case <-entry.done:
	case <-ctx.Done():
		return nil, Result{}, false, ctx.Err()
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	if !entry.ok {
		return nil, Result{}, false, nil
	}
	s.stats.Hits++
	s.stats.Coalesced++
	return entry.payload, entry.result, true, nil
}

// hit records a follower served from an already completed entry.
func (s *StepReadCache) hit() {
	s.mu.Lock()
	s.stats.Hits++
	s.mu.Unlock()
}

// replyScope extracts the observation scope of a reviewed read reply: every
// cacheable reply is `oneof outcome { <snapshot> observed = 1; ... }` whose
// snapshot's first field is an ObservationContext. Refusals and
// unavailability carry none and report false.
func replyScope(reply proto.Message) (readScope, bool) {
	if reply == nil {
		return readScope{}, false
	}
	var found *c.ObservationContext
	reply.ProtoReflect().Range(func(fd protoreflect.FieldDescriptor, v protoreflect.Value) bool {
		if fd.Kind() != protoreflect.MessageKind || fd.IsList() || fd.IsMap() {
			return true
		}
		outcome := v.Message()
		contextField := outcome.Descriptor().Fields().ByName("context")
		if contextField == nil || contextField.Kind() != protoreflect.MessageKind || !outcome.Has(contextField) {
			return true
		}
		observation, ok := outcome.Get(contextField).Message().Interface().(*c.ObservationContext)
		if ok {
			found = observation
			return false
		}
		return true
	})
	if found == nil || found.Identity == nil || found.Tick == nil || found.NativeGeneration == nil || found.Identity.LoadToken == nil {
		return readScope{}, false
	}
	return readScope{load: found.Identity.GetLoadToken(), tick: found.GetTick(), generation: found.GetNativeGeneration()}, true
}
