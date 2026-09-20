package observation

import (
	"context"
	"sort"
	"sync"
)

// DefinitionPool remembers every project definition name the planners of a
// scheduler have asked for beyond the default planning census. A step's
// planners each need a different handful (the kitchen its dispenser and
// hopper, the fields its crops and lamps), and each handful is its own
// read_colony_facts request carrying the whole ~500 KB colony reply; with
// the pool every planner requests the union instead, so the requests share
// one key and the step's read cache serves one native round trip to all of
// them (#599). The pool lives as long as the scheduler: the first step each
// planner runs still reads its own names, every later step reads once.
type DefinitionPool struct {
	mu    sync.Mutex
	names map[string]bool
}

// definitionPoolLimit is the most names a pooled request carries, the
// colony read's own bound; past it a planner reads only its own names.
const definitionPoolLimit = 256

type definitionPoolKey struct{}

// NewDefinitionPool returns an empty pool.
func NewDefinitionPool() *DefinitionPool {
	return &DefinitionPool{names: map[string]bool{}}
}

// WithDefinitionPool attaches pool to ctx for the project definition reads
// under it.
func WithDefinitionPool(ctx context.Context, pool *DefinitionPool) context.Context {
	if pool == nil {
		return ctx
	}
	return context.WithValue(ctx, definitionPoolKey{}, pool)
}

// DefinitionPoolFrom returns the pool ctx carries, or nil.
func DefinitionPoolFrom(ctx context.Context) *DefinitionPool {
	pool, _ := ctx.Value(definitionPoolKey{}).(*DefinitionPool)
	return pool
}

// Request adds wanted to the pool and returns the names one read should
// carry: every pooled name absent from the default census, sorted, or
// wanted alone when the union would exceed the read's bound. A nil pool
// returns wanted.
func (p *DefinitionPool) Request(wanted []string, census map[string]bool) []string {
	if p == nil {
		return wanted
	}
	p.mu.Lock()
	defer p.mu.Unlock()
	for _, name := range wanted {
		p.names[name] = true
	}
	request := make([]string, 0, len(p.names))
	for name := range p.names {
		if !census[name] {
			request = append(request, name)
		}
	}
	if len(request) > definitionPoolLimit {
		return wanted
	}
	sort.Strings(request)
	return request
}
