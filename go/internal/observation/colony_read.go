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

// ColonyReading is a paused, same-tick observation. It conveys facts, not
// authority; callers must still check their player direction before committing.
type ColonyReading struct {
	Projection            ColonyProjection
	StartedAt, ObservedAt time.Time
	Receipt               bridge.Result
}

func sameColonyBoundary(actual, expected Identity) bool {
	a, ak := actual.NativeGeneration.Value()
	b, bk := expected.NativeGeneration.Value()
	paused, known := actual.Paused.Value()
	return actual.SameContext(expected) && actual.Tick == expected.Tick && ak && bk && a == b && known && paused
}

// ObserveColony requires an externally observed paused identity and reads
// the facts under it. Every reply carries an ObservationContext, so the
// facts themselves prove they were read at the expected load, map, tick and
// generation; no identity read brackets them. Missing generations cannot
// confirm the boundary, and the pause state is the caller's observation.
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
	// Colony facts do not carry pause state; that fact is the caller's.
	observed := projection.Identity
	observed.Paused = expected.Paused
	if !sameColonyBoundary(observed, expected) {
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
