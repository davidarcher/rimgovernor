package buildingruntime

import (
	"context"
	"errors"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	"google.golang.org/protobuf/proto"
)

// ErrCheckpoint means a trusted checkpoint save was refused: no attempt was
// made, or native reported an uncertain/failed outcome. It is never silently
// treated as success; the caller keeps whatever save state it already had.
var ErrCheckpoint = errors.New("checkpoint refused")

// LifecycleSaver is the trusted single-save native capability, held
// separately from ordinary reads the same way NativeAuthority is.
type LifecycleSaver interface {
	Save(context.Context, *l.SaveRequest) (*l.SaveReply, bridge.Result, error)
}

// CheckpointRequest names the caller's expected live world/direction and the
// native save name. ExpectedTick is optional; when present it is asserted
// against the currently observed tick before native persists anything.
type CheckpointRequest struct {
	Expected        domain.GenerationSnapshot
	SaveName        string
	ExpectedTick    domain.Tick
	HasExpectedTick bool
}

// CheckpointResult is the durable evidence of one trusted, pause-gated save.
// It never means load/reconnect happened; those remain a separate capability.
type CheckpointResult struct {
	Snapshot domain.GenerationSnapshot
	Tick     domain.Tick
	SaveName string
	Paused   bool
}

// Checkpoint drains owned writers under the control gate, refuses a stale
// direction or foreign colony/load/map, requests one native save and
// validates the reply before returning success. Authority is never revoked
// and the profile lock is never released: play continues in Manual after a
// checkpoint. An uncertain native outcome is always reported as a refusal,
// never silently accepted, matching the Python "checkpoint was not
// published" behavior this replaces.
func (control *Control) Checkpoint(ctx context.Context, save LifecycleSaver, request CheckpointRequest) (CheckpointResult, error) {
	if save == nil || request.Expected.Validate() != nil || request.Expected.Direction == 0 || !boundary.ValidID(request.SaveName) {
		return CheckpointResult{}, ErrCheckpoint
	}
	control.mu.Lock()
	if control.closing || !control.liveLocked() {
		control.mu.Unlock()
		return CheckpointResult{}, ErrCheckpoint
	}
	current, epoch := control.snapshot, control.epoch
	control.mu.Unlock()
	if !boundary.World(current, request.Expected) || current.Direction != request.Expected.Direction {
		return CheckpointResult{}, ErrCheckpoint
	}
	call, done, err := control.enter(ctx, epoch)
	if err != nil {
		return CheckpointResult{}, errors.Join(ErrCheckpoint, err)
	}
	defer done()
	if control.config.CleanupWrites != nil {
		if err = control.config.CleanupWrites(call); err != nil {
			return CheckpointResult{}, errors.Join(ErrCheckpoint, err)
		}
	}
	if err = call.Err(); err != nil {
		return CheckpointResult{}, errors.Join(ErrCheckpoint, err)
	}
	req := &l.SaveRequest{
		Player: &l.PlayerLifecycleContext{
			Identity:        controlIdentity(current),
			PlayerDirection: proto.Uint64(uint64(current.Direction)),
			RequestId:       proto.String(fmt.Sprintf("checkpoint-%s-%d", control.namespace, time.Now().UnixNano())),
		},
		SaveName: proto.String(request.SaveName),
	}
	if request.HasExpectedTick {
		req.ExpectedTick = proto.Int64(int64(request.ExpectedTick))
	}
	reply, _, err := save.Save(call, req)
	if err != nil {
		return CheckpointResult{}, errors.Join(ErrCheckpoint, err)
	}
	completed := reply.GetCompleted()
	if completed == nil {
		return CheckpointResult{}, ErrCheckpoint
	}
	return CheckpointResult{Snapshot: current, Tick: domain.Tick(completed.Context.GetTick()), SaveName: completed.GetSaveName(), Paused: completed.GetPaused()}, nil
}
