package buildingruntime

import (
	"context"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// clockAdmissionWarm holds only the short-lived observation families. The
// admitting step still reads live clock status and validates the paused scope.
type clockAdmissionWarm struct {
	snapshot      *o.BundleSnapshot
	at            time.Time
	invalidations uint64
}

// warmAdmission runs after the stop page's invalidations and before its wake.
// Failure only loses the optimization: the step then reads its usual bundle.
func (s *ClockScheduler) warmAdmission(ctx context.Context, observed *c.ObservationContext) {
	ctx = bridge.WithStepReadCache(ctx, bridge.NewChildReadCache(s.facts.cache))
	at := s.clock.Now()
	invalidations := s.facts.cache.Stats().Invalidations
	reply, _, err := s.native.ReadBundle(ctx, &o.BundleRequest{
		Scope:     &o.ReadScope{ExpectedIdentity: proto.Clone(observed.Identity).(*c.Identity)},
		Emergency: proto.Bool(true), ColonistPawns: proto.Bool(true), ColonistPawnFields: &o.PawnFields{},
	})
	if err != nil {
		return
	}
	v := reply.GetObserved()
	if v == nil || !v.GetPaused() || v.Emergency == nil || !proto.Equal(v.Context, observed) || s.facts.cache.Stats().Invalidations != invalidations {
		return
	}
	s.admissionWarm.Store(&clockAdmissionWarm{snapshot: proto.Clone(v).(*o.BundleSnapshot), at: at, invalidations: invalidations})
}

func (s *ClockScheduler) readStepBundle(ctx context.Context, request *o.BundleRequest) (*o.BundleReply, error) {
	warm := s.admissionWarm.Load()
	if warm == nil || s.clock.Now().Sub(warm.at) > s.config.MaxAge || s.facts.cache.Stats().Invalidations != warm.invalidations {
		reply, _, err := s.native.ReadBundle(ctx, request)
		return reply, err
	}
	reduced := proto.Clone(request).(*o.BundleRequest)
	reduced.Emergency, reduced.ColonistPawns = proto.Bool(false), proto.Bool(false)
	reply, _, err := s.native.ReadBundle(ctx, reduced)
	if err != nil {
		return nil, err
	}
	v := reply.GetObserved()
	if v != nil && v.GetPaused() && proto.Equal(v.Context, warm.snapshot.Context) &&
		s.clock.Now().Sub(warm.at) <= s.config.MaxAge && s.facts.cache.Stats().Invalidations == warm.invalidations && s.admissionWarm.CompareAndSwap(warm, nil) {
		v.Emergency = proto.Clone(warm.snapshot.Emergency).(*o.StatusSnapshot)
		// Pawn detail was seeded into the cross-step cache by the warm read;
		// missing or invalidated detail falls back to the dedicated read.
		return reply, nil
	}
	s.admissionWarm.CompareAndSwap(warm, nil)
	reply, _, err = s.native.ReadBundle(ctx, request)
	return reply, err
}
