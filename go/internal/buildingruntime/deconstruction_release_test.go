package buildingruntime

import (
	"context"
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
	"testing"
)

type releaseDeconstructionWriter struct {
	attempts  []*a.WritePrecondition
	uncertain bool
}

func (*releaseDeconstructionWriter) ApplyDeconstruction(context.Context, *a.WritePrecondition, string) (*o.ExecuteReply, bridge.Result, error) {
	return nil, bridge.Result{}, errors.New("unexpected designation")
}
func (w *releaseDeconstructionWriter) ReleaseDeconstructions(_ context.Context, p *a.WritePrecondition) (*o.ExecuteReply, bridge.Result, error) {
	w.attempts = append(w.attempts, proto.Clone(p).(*a.WritePrecondition))
	if w.uncertain {
		return &o.ExecuteReply{Outcome: &o.ExecuteReply_Receipt{Receipt: &r.Receipt{Outcome: &r.Receipt_Uncertain{Uncertain: &r.Uncertain{}}}}}, bridge.Result{}, nil
	}
	return &o.ExecuteReply{Outcome: &o.ExecuteReply_Receipt{Receipt: &r.Receipt{Outcome: &r.Receipt_Applied{Applied: &r.Applied{Observed: &r.EffectEvidence{Effect: &r.EffectEvidence_ReleaseDeconstructions{ReleaseDeconstructions: &r.ReleaseDeconstructionsEffect{ReleasedCount: proto.Int32(1)}}}}}}}}, bridge.Result{}, nil
}

func TestManualReleasesDeconstructionsBeforeRevokingAndRetriesSameAttempt(t *testing.T) {
	control, native, sink, _ := controlFixture(t, nil)
	snapshot, err := control.Acquire(context.Background(), controlScope())
	if err != nil {
		t.Fatal(err)
	}
	writer := &releaseDeconstructionWriter{uncertain: true}
	release := releaseDeconstructions(writer, "session")
	control.config.BeforeRevoke = func(ctx context.Context, id *c.Identity, generation uint64) error {
		if sink.enabled() || native.revokes.Load() != 0 || generation != uint64(snapshot.Native) {
			t.Fatal("release occurred outside disabled-local/active-native window")
		}
		return release(ctx, id, generation)
	}
	if err = control.Manual(context.Background()); !errors.Is(err, ErrControl) || native.revokes.Load() != 0 {
		t.Fatal(err, native.revokes.Load())
	}
	writer.uncertain = false
	if err = control.Manual(context.Background()); err != nil {
		t.Fatal(err)
	}
	if len(writer.attempts) != 2 || !proto.Equal(writer.attempts[0], writer.attempts[1]) || native.revokes.Load() != 1 {
		t.Fatal(writer.attempts, native.revokes.Load())
	}
}
