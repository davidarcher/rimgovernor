package buildingruntime

import (
	"context"
	"errors"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	"google.golang.org/protobuf/proto"
)

// ErrLoad means a native load was refused: no attempt was made, the request
// was malformed, or the poll deadline elapsed before a definite outcome (map
// readiness or a typed refusal) arrived. It is never silently treated as a
// completed load.
var ErrLoad = errors.New("load refused")

// LifecycleLoader is the trusted async-load native capability. Unlike
// NativeAuthority, a Load has no existing colony to hold writer authority
// over yet, so this is intentionally not a Control method: there is no lease,
// gate or generation to reuse before a map exists. LoadBoundary is the
// simplest orchestration that correctly fits an async start/poll RPC pair.
type LifecycleLoader interface {
	Load(context.Context, *l.LoadRequest) (*l.LoadReply, bridge.Result, error)
	ReadLoad(context.Context, string) (*l.LoadReply, bridge.Result, error)
}

// LoadBoundary issues one native load and polls ReadLoad to a definite
// outcome. It holds no state between calls: the caller owns request_id
// uniqueness and deciding whether/when to retry a refused load.
type LoadBoundary struct {
	Native LifecycleLoader
	// PollInterval paces ReadLoad polling; it defaults to 200ms when zero.
	PollInterval time.Duration
}

// LoadRequest names the save to load and the caller's expectations. ExpectedColony,
// when set, is asserted against the post-load colony id: it is the one part of
// identity that legitimately persists across a load of the same save (the load
// token is always freshly issued, matching RimWorld's own load-completion
// contract). RequestID and SaveName must be caller-supplied valid identifiers.
// RequireVisualReadiness asks native to hold LoadCompleted until the loaded
// map has actually been drawn at least once (MapVisualReadyTracker), not
// merely reached MAP readiness (data in memory); it defaults to MAP.
type LoadRequest struct {
	RequestID              string
	SaveName               string
	ExpectedColony         domain.ColonyID
	HasExpectedColony      bool
	ExpectedInstanceID     string
	PlayerDirection        domain.DirectionID
	HasPlayerDirection     bool
	Deadline               time.Time
	RequireVisualReadiness bool
}

// LoadResult is the durable evidence of one completed native load. VisualReady
// reflects the readiness native actually reported reaching (Readiness_VISUAL),
// which is only ever true when the request asked for it: native never
// completes a MAP-only request past visual readiness.
type LoadResult struct {
	Snapshot    domain.GenerationSnapshot
	Tick        domain.Tick
	SaveName    string
	Paused      bool
	VisualReady bool
}

// Load starts a native load and polls ReadLoad until map readiness, a typed
// refusal (superseded/failure), or the caller's deadline elapses. A superseded
// outcome and a failure are both returned as errors satisfying errors.Is(err,
// ErrLoad); callers needing the richer native detail can unwrap
// *bridge.LoadSuperseded or *bridge.NativeFailure.
func (boundary_ *LoadBoundary) Load(ctx context.Context, request LoadRequest) (LoadResult, error) {
	if boundary_ == nil || boundary_.Native == nil || !boundary.ValidID(request.RequestID) || !boundary.ValidID(request.SaveName) {
		return LoadResult{}, ErrLoad
	}
	if request.HasExpectedColony && !boundary.ValidID(string(request.ExpectedColony)) {
		return LoadResult{}, ErrLoad
	}
	if request.HasPlayerDirection && request.PlayerDirection == 0 {
		return LoadResult{}, ErrLoad
	}
	if !request.Deadline.IsZero() && !time.Now().Before(request.Deadline) {
		return LoadResult{}, ErrLoad
	}
	readiness := l.Readiness_READINESS_MAP
	if request.RequireVisualReadiness {
		readiness = l.Readiness_READINESS_VISUAL
	}
	req := &l.LoadRequest{
		RequestId: proto.String(request.RequestID),
		SaveName:  proto.String(request.SaveName),
		Readiness: readiness.Enum(),
	}
	if request.ExpectedInstanceID != "" {
		req.ExpectedInstanceId = proto.String(request.ExpectedInstanceID)
	}
	if request.HasPlayerDirection {
		req.PlayerDirection = proto.Uint64(uint64(request.PlayerDirection))
	}
	reply, _, err := boundary_.Native.Load(ctx, req)
	if err != nil {
		if isLoadPending(err) {
			return boundary_.poll(ctx, request)
		}
		return LoadResult{}, errors.Join(ErrLoad, err)
	}
	result, ok := completedResult(reply, request)
	if !ok {
		return LoadResult{}, ErrLoad
	}
	return result, nil
}

func (boundary_ *LoadBoundary) poll(ctx context.Context, request LoadRequest) (LoadResult, error) {
	interval := boundary_.PollInterval
	if interval <= 0 {
		interval = 200 * time.Millisecond
	}
	for {
		if !request.Deadline.IsZero() && !time.Now().Before(request.Deadline) {
			return LoadResult{}, errors.Join(ErrLoad, context.DeadlineExceeded)
		}
		if err := ctx.Err(); err != nil {
			return LoadResult{}, errors.Join(ErrLoad, err)
		}
		wait := interval
		if !request.Deadline.IsZero() {
			if remaining := time.Until(request.Deadline); remaining < wait {
				wait = remaining
			}
		}
		if wait > 0 {
			timer := time.NewTimer(wait)
			select {
			case <-ctx.Done():
				timer.Stop()
				return LoadResult{}, errors.Join(ErrLoad, ctx.Err())
			case <-timer.C:
			}
		}
		reply, _, err := boundary_.Native.ReadLoad(ctx, request.RequestID)
		if err != nil {
			if isLoadPending(err) {
				continue
			}
			return LoadResult{}, errors.Join(ErrLoad, err)
		}
		result, ok := completedResult(reply, request)
		if !ok {
			return LoadResult{}, ErrLoad
		}
		return result, nil
	}
}

func isLoadPending(err error) bool {
	var pending *bridge.LoadPending
	return errors.As(err, &pending)
}

// completedResult validates a LoadCompleted reply against the caller's
// expectations. A colony mismatch, when ExpectedColony was asked for, is
// refused rather than accepted: identity/load/map moved away from what this
// request believed it was loading.
func completedResult(reply *l.LoadReply, request LoadRequest) (LoadResult, bool) {
	completed := reply.GetCompleted()
	if completed == nil || completed.Loaded == nil || completed.Loaded.Context == nil {
		return LoadResult{}, false
	}
	context := completed.Loaded.Context
	identity := context.Identity
	if identity == nil {
		return LoadResult{}, false
	}
	if request.HasExpectedColony && domain.ColonyID(identity.GetColonyId()) != request.ExpectedColony {
		return LoadResult{}, false
	}
	snapshot := domain.GenerationSnapshot{
		Colony: domain.ColonyID(identity.GetColonyId()),
		Load:   domain.LoadID(identity.GetLoadToken()),
		Map:    domain.MapID(identity.GetMapId()),
	}
	return LoadResult{
		Snapshot:    snapshot,
		Tick:        domain.Tick(context.GetTick()),
		SaveName:    completed.GetSaveName(),
		Paused:      completed.Loaded.GetPaused(),
		VisualReady: completed.GetReadiness() == l.Readiness_READINESS_VISUAL,
	}, true
}
