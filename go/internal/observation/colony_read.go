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

type ColonySource interface {
	Identity(context.Context) (*l.IdentityReply, bridge.Result, error)
	ReadColonyFacts(context.Context, *c.Identity, bool, []string) (*o.ColonyFactsReply, bridge.Result, error)
}

// ColonyReading is a paused, same-tick observation interval. It conveys facts,
// not authority; callers must still check their player direction before committing.
type ColonyReading struct {
	Projection            ColonyProjection
	StartedAt, ObservedAt time.Time
	Receipts              [3]bridge.Result
}

func sameColonyBoundary(actual, expected Identity) bool {
	a, ak := actual.NativeGeneration.Value()
	b, bk := expected.NativeGeneration.Value()
	paused, known := actual.Paused.Value()
	return actual.SameContext(expected) && actual.Tick == expected.Tick && ak && bk && a == b && known && paused
}

// ObserveColony requires an externally observed paused identity, then brackets
// the facts read with fresh identities. Missing generations cannot confirm it.
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
	first, receipt, err := source.Identity(ctx)
	result.Receipts[0] = receipt
	if err != nil {
		return result, err
	}
	before, err := DecodeIdentity(first)
	if err != nil {
		return result, err
	}
	if !sameColonyBoundary(before, expected) {
		return result, ErrChanged
	}
	id := &c.Identity{ColonyId: proto.String(string(expected.Colony)), LoadToken: proto.String(string(expected.Load)), MapId: proto.Int32(int32(expected.Map))}
	reply, receipt, err := source.ReadColonyFacts(ctx, id, planning, append([]string(nil), definitions...))
	result.Receipts[1] = receipt
	if err != nil {
		return result, err
	}
	projection, err := DecodeColony(reply, expected)
	if err != nil {
		return result, err
	}
	// Colony facts do not carry pause state; that fact belongs to the brackets.
	observed := projection.Identity
	observed.Paused = before.Paused
	if !sameColonyBoundary(observed, expected) {
		return result, ErrChanged
	}
	last, receipt, err := source.Identity(ctx)
	result.Receipts[2] = receipt
	if err != nil {
		return result, err
	}
	after, err := DecodeIdentity(last)
	if err != nil {
		return result, err
	}
	if !sameColonyBoundary(after, expected) {
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
