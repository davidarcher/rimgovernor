package executor

import (
	"context"
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store/storetest"
	"testing"
)

type requestNative struct {
	f     *fixture
	read  CommsTradeInspection
	lost  bool
	calls int
}

func (n *requestNative) InspectCommsTradeRequest(_ context.Context, target Target) (CommsTradeInspection, error) {
	read := n.read
	read.Snapshot = target.Snapshot
	read.StartedAt, read.ObservedAt = n.f.clock.Now(), n.f.clock.Now()
	return read, nil
}
func (n *requestNative) WriteCommsTradeRequest(_ context.Context, p Placement) (Receipt, error) {
	n.calls++
	if n.lost {
		return Receipt{}, errors.New("lost response")
	}
	return Receipt{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: p.Snapshot, Kind: domain.ReceiptAccepted}, nil
}
func requestFixture(t *testing.T) (*fixture, *requestNative) {
	t.Helper()
	action, err := domain.NewCommsTradeRequestAction("request", domain.CommsTradeRequest{Kind: domain.TradeRequestOrbital, Faction: "ally", TraderKind: "bulk", Console: "console", Negotiator: "pawn", ExpectedLastRequestTick: -900000})
	if err != nil {
		t.Fatal(err)
	}
	f := newFixtureAt(t, storetest.Path(t), action)
	n := &requestNative{f: f, read: CommsTradeInspection{Tick: 100, RequestKnown: true, LastRequestTick: -900000, Eligible: true}}
	if err = f.executor.EnableCommsTradeRequests(n); err != nil {
		t.Fatal(err)
	}
	return f, n
}
func TestCommsTradeRequestReconcilesWithoutResending(t *testing.T) {
	for _, lost := range []bool{false, true} {
		t.Run(map[bool]string{false: "queued_receipt", true: "lost_receipt"}[lost], func(t *testing.T) {
			f, n := requestFixture(t)
			n.lost = lost
			ctx := context.Background()
			out, err := f.executor.RunBatch(ctx, f.plan.ID(), []domain.ActionID{f.action.ID()})
			if err != nil || len(out) != 1 || !out[0].Result.Progress.View().Unresolved {
				t.Fatal(out, err)
			}
			n.read.MatchingWork = true
			out, err = f.executor.RunBatch(ctx, f.plan.ID(), []domain.ActionID{f.action.ID()})
			if err != nil || out[0].Err != nil || out[0].Result.Progress.View().Stage != domain.AwaitingObservation || n.calls != 1 {
				t.Fatal(out, err, n.calls)
			}
			n.read.LastRequestTick = 100
			n.read.MatchingWork = false
			n.read.MatchingArrival = true
			out, err = f.executor.RunBatch(ctx, f.plan.ID(), []domain.ActionID{f.action.ID()})
			if err != nil || out[0].Err != nil || out[0].Result.Progress.View().Stage != domain.Completed || n.calls != 1 {
				t.Fatal(out, err, n.calls)
			}
			_, err = f.executor.RunBatch(ctx, f.plan.ID(), []domain.ActionID{f.action.ID()})
			if err != nil || n.calls != 1 {
				t.Fatal(err, n.calls)
			}
		})
	}
}
func TestCommsTradeRequestInterruptedAndAmbiguousWork(t *testing.T) {
	for _, lost := range []bool{false, true} {
		f, n := requestFixture(t)
		n.lost = lost
		ctx := context.Background()
		_, err := f.executor.RunBatch(ctx, f.plan.ID(), []domain.ActionID{f.action.ID()})
		if err != nil {
			t.Fatal(err)
		}
		out, err := f.executor.RunBatch(ctx, f.plan.ID(), []domain.ActionID{f.action.ID()})
		if err != nil || out[0].Err != nil || n.calls != 1 {
			t.Fatal(out, err, n.calls)
		}
		want := domain.Unsuccessful
		if lost {
			want = domain.AwaitingObservation
		}
		if out[0].Result.Progress.View().Stage != want {
			t.Fatal(out[0].Result.Progress.View())
		}
	}
}
func TestCommsTradeRequestLiveGuardChangesCancelBeforeDispatch(t *testing.T) {
	f, n := requestFixture(t)
	n.read.Eligible = false
	out, err := f.executor.RunBatch(context.Background(), f.plan.ID(), []domain.ActionID{f.action.ID()})
	if err != nil || out[0].Err != nil || out[0].Result.Progress.View().Stage != domain.Cancelled || n.calls != 0 {
		t.Fatal(out, err, n.calls)
	}
}

func TestCommsTradeRequestArrivalAndNoShow(t *testing.T) {
	request := domain.CommsTradeRequest{Kind: domain.TradeRequestOrbital, ExpectedLastRequestTick: -900000}
	view := domain.ProgressView{Tick: 100}
	for _, tc := range []struct {
		name   string
		read   CommsTradeInspection
		effect domain.Effect
	}{
		{"native_queue", CommsTradeInspection{Tick: 101, RequestKnown: true, LastRequestTick: 100, MatchingArrival: true}, domain.EffectCompleted},
		{"already_arrived", CommsTradeInspection{Tick: 6000, RequestKnown: true, LastRequestTick: 100, MatchingSeller: true}, domain.EffectCompleted},
		{"no_show", CommsTradeInspection{Tick: 6000, RequestKnown: true, LastRequestTick: 100}, domain.EffectUnsuccessful},
		{"missing_target", CommsTradeInspection{Tick: 101}, domain.EffectUnsuccessful},
		{"changed_tick_only", CommsTradeInspection{Tick: 101, RequestKnown: true, LastRequestTick: 100}, domain.EffectUnknown},
	} {
		t.Run(tc.name, func(t *testing.T) {
			effect, _ := CommsTradeRequestEffect(request, view, tc.read)
			if effect != tc.effect {
				t.Fatal(effect, tc.effect)
			}
		})
	}
}

func TestCommsTradeRequestDelayedEvidenceKeepsDispatchAnchor(t *testing.T) {
	f, n := requestFixture(t)
	n.lost = true
	ctx := context.Background()
	if _, err := f.executor.RunBatch(ctx, f.plan.ID(), []domain.ActionID{f.action.ID()}); err != nil {
		t.Fatal(err)
	}
	n.read.LastRequestTick = 100
	n.read.Tick = 200
	if _, err := f.executor.RunBatch(ctx, f.plan.ID(), []domain.ActionID{f.action.ID()}); err != nil {
		t.Fatal(err)
	}
	if f.progress(t).Tick != 100 {
		t.Fatal("unknown read moved the payment anchor", f.progress(t))
	}
	n.read.Tick = 3000
	n.read.MatchingSeller = true
	out, err := f.executor.RunBatch(ctx, f.plan.ID(), []domain.ActionID{f.action.ID()})
	if err != nil || out[0].Err != nil || out[0].Result.Progress.View().Stage != domain.Completed || n.calls != 1 {
		t.Fatal(out, err, n.calls)
	}
}
