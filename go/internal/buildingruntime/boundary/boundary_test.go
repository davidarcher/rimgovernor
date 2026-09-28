package boundary

import (
	"context"
	"errors"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

// A building intent is sent under an action-keyed intent key; applied is
// accepted, a native refusal is refused, and an attempt conflict or a lost
// lease stays unknown.
func TestWriteIntentReceiptKinds(t *testing.T) {
	t.Parallel()
	b, f := NewFixture(t)
	out, err := writeOne(b, f.Placement)
	if err != nil || out.Kind != domain.ReceiptAccepted || f.Places != 1 || len(f.LastKeys) != 1 || !strings.HasPrefix(f.LastKeys[0], string(f.Placement.Action.ID())+"/") {
		t.Fatal("applied intent not accepted", err, out, f.LastKeys)
	}
	b, f = NewFixture(t)
	f.Refuse = "cell blocked"
	if out, err = writeOne(b, f.Placement); err != nil || out.Kind != domain.ReceiptRefused {
		t.Fatal("native refusal not refused", err, out)
	}
	b, f = NewFixture(t)
	f.LeaseErr = executor.ErrAuthority
	if out, err = writeOne(b, f.Placement); err == nil || out.Kind != domain.ReceiptUnknown || f.Places != 0 {
		t.Fatal("lease error dispatched")
	}
	b, f = NewFixture(t)
	f.PlaceErr = &bridge.NativeFailure{Value: &c.Failure{Code: c.FailureCode_FAILURE_CODE_ATTEMPT_CONFLICT.Enum()}}
	if out, err = writeOne(b, f.Placement); err == nil || out.Kind != domain.ReceiptUnknown {
		t.Fatal("call failure released uncertainty", err, out)
	}
}

func writeOne(b *Boundary, p executor.Placement) (executor.Receipt, error) {
	out, err := b.WriteIntents(context.Background(), []executor.Placement{p})
	return out[0], err
}

// An unknown ledger lookup is the trace of a dispatch that timed out before
// native admission (#71): it resolves as complete absence at the lookup's
// own tick. An admitted in-flight entry still holds, and a lookup context
// from before the dispatch or another native generation is never evidence.
func TestUnadmittedResolvesOnlyAnUnknownLedgerLookup(t *testing.T) {
	t.Parallel()
	_, f := NewFixture(t)
	p := f.Placement
	unknown := func(tick int64, generation uint64) *r.LookupReply {
		return &r.LookupReply{Outcome: &r.LookupReply_Unknown{Unknown: &r.UnknownAttempt{Context: &c.ObservationContext{Identity: Identity(p.Snapshot), Tick: proto.Int64(tick), NativeGeneration: proto.Uint64(generation)}}}}
	}
	out, err := Unadmitted(unknown(14, 1), p, p.Snapshot)
	if err != nil || out.Effect != domain.EffectAbsent || out.Tick != 14 || out.Causality != domain.AfterDispatch || out.Action != p.Action.ID() || out.Attempt != p.Attempt || !out.Snapshot.Matches(p.Snapshot) {
		t.Fatal("unknown lookup not resolved as absent", err, out)
	}
	if out, err = Unadmitted(unknown(10, 1), p, p.Snapshot); err != nil || out.Effect != domain.EffectAbsent || out.Tick != 10 {
		t.Fatal("same-tick lookup rejected", err, out)
	}
	if _, err = Unadmitted(unknown(9, 1), p, p.Snapshot); !errors.Is(err, executor.ErrEvidence) {
		t.Fatal("pre-dispatch tick accepted", err)
	}
	if _, err = Unadmitted(unknown(14, 2), p, p.Snapshot); !errors.Is(err, executor.ErrAuthority) {
		t.Fatal("foreign native generation accepted", err)
	}
	inFlight := &r.LookupReply{Outcome: &r.LookupReply_InFlight{InFlight: &r.InFlight{Attempt: f.Receipt.Attempt, AdmittedContext: f.Receipt.AdmittedContext}}}
	if _, err = Unadmitted(inFlight, p, p.Snapshot); !errors.Is(err, executor.ErrHeld) {
		t.Fatal("in-flight attempt not held", err)
	}
	if _, err = Unadmitted(&r.LookupReply{}, p, p.Snapshot); !errors.Is(err, executor.ErrEvidence) {
		t.Fatal("empty lookup accepted", err)
	}
}
