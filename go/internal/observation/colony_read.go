package observation

import (
	"context"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// ColonySource is the planner-side colony read. Identity is the paused
// identity a planner observes before it plans; ObserveColony itself reads
// only the facts and validates them by their ObservationContext.
type ColonySource interface {
	Identity(context.Context) (*l.IdentityReply, bridge.Result, error)
	ReadColonyFacts(context.Context, *c.Identity, bool, []string) (*o.ColonyFactsReply, bridge.Result, error)
}

// ColonyReading is an observation bound to the caller's expected tick: read
// at it, or under a running clock within domain.PlanningTickTolerance past
// it. It conveys facts, not authority; callers must still check their
// player direction before committing.
type ColonyReading struct {
	Projection            ColonyProjection
	StartedAt, ObservedAt time.Time
	Receipt               bridge.Result
}

// sameColonyBoundary is the freshness rule every routine read applies to a
// reply's context: the expected load, map and native generation, at a tick
// fresh for the expected one (domain.Tick.FreshFor). The clock need not be
// stopped: a step under a running window reads within the tolerance (#243).
func sameColonyBoundary(actual, expected Identity) bool {
	return sameColonyContext(actual, expected) && actual.Tick.FreshFor(expected.Tick)
}

// cachedColonyBoundary is sameColonyBoundary for a read the step's fact
// cache may serve: the row may also sit behind the expected tick by up to
// its family's tolerance (bridge.FactFamily.Fresh), the same rule the cache
// serves it under. Under a running window the step's later reads come from
// the cache, so a row behind the anchor is not a changed context (#306).
func cachedColonyBoundary(actual, expected Identity, family bridge.FactFamily) bool {
	return sameColonyContext(actual, expected) && (actual.Tick.FreshFor(expected.Tick) || family.Fresh(int64(actual.Tick), int64(expected.Tick)))
}

func sameColonyContext(actual, expected Identity) bool {
	a, ak := actual.NativeGeneration.Value()
	b, bk := expected.NativeGeneration.Value()
	return actual.SameContext(expected) && ak && bk && a == b
}

// ObserveColony requires an externally observed identity and reads the
// facts under it. Every reply carries an ObservationContext, so the facts
// themselves prove they were read at the expected load, map and generation
// and within the tolerance of the expected tick; no identity read brackets
// them. Missing generations cannot confirm the boundary.
func ObserveColony(ctx context.Context, source ColonySource, clock Clock, expected Identity, maxAge time.Duration, planning bool, definitions []string) (ColonyReading, error) {
	var result ColonyReading
	if source == nil || clock == nil || maxAge <= 0 || expected.Validate() != nil {
		return result, ErrContract
	}
	if !sameColonyBoundary(expected, expected) {
		return result, ErrChanged
	}
	result.StartedAt = clock.Now()
	if result.StartedAt.IsZero() {
		return result, ErrStale
	}
	if err := ctx.Err(); err != nil {
		return result, err
	}
	id := &c.Identity{ColonyId: proto.String(string(expected.Colony)), LoadToken: proto.String(string(expected.Load)), MapId: proto.Int32(int32(expected.Map))}
	reply, receipt, err := source.ReadColonyFacts(ctx, id, planning, append([]string(nil), definitions...))
	result.Receipt = receipt
	if err != nil {
		return result, err
	}
	projection, err := DecodeColony(reply, expected)
	if err != nil {
		return result, err
	}
	if planning {
		if err := fillPlanningWindow(ctx, reply, id, &projection); err != nil {
			return result, err
		}
	}
	// Colony facts do not carry pause state; that fact is the caller's.
	observed := projection.Identity
	observed.Paused = expected.Paused
	if !cachedColonyBoundary(observed, expected, bridge.FactColony) {
		return result, ErrChanged
	}
	result.ObservedAt = clock.Now()
	if result.ObservedAt.Before(result.StartedAt) || result.ObservedAt.Sub(result.StartedAt) > maxAge {
		return result, ErrStale
	}
	if err := ctx.Err(); err != nil {
		return result, err
	}
	result.Projection = projection
	return result, nil
}
