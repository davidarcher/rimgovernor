package domain

import "testing"

// pendingCancelled is a dispatched action whose receipt was accepted and
// whose effect stayed pending, then cancelled by its planner (#291).
func pendingCancelled(t *testing.T) (Progress, GenerationSnapshot) {
	t.Helper()
	p, s := dispatched(t)
	p, err := p.RecordReceipt(1, ReceiptAccepted)
	if err != nil {
		t.Fatal(err)
	}
	p, err = p.Observe(Observation{Action: p.View().Action, Attempt: 1, Snapshot: s, Tick: 20, Effect: EffectPending, Causality: AfterDispatch}, s)
	if err != nil {
		t.Fatal(err)
	}
	p, err = p.Cancel()
	if err != nil {
		t.Fatal(err)
	}
	return p, s
}

func TestWithdrawOpensFreshAttemptOfCancelledPendingDispatch(t *testing.T) {
	p, s := pendingCancelled(t)
	next, err := p.Withdraw(s, 30)
	if err != nil {
		t.Fatal(err)
	}
	v := next.View()
	if v.Stage != Cancelled || !v.Unresolved || v.Attempt != 2 || v.Tick != 30 {
		t.Fatalf("withdrawal did not open a fresh cancelled attempt: %+v", v)
	}
	if _, known := v.Receipt.Value(); known {
		t.Fatal("withdrawal kept the earlier receipt")
	}
	if _, known := v.Effect.Value(); known {
		t.Fatal("withdrawal kept the earlier effect")
	}
	if !GoalWorkOpen([]Progress{next}) {
		t.Fatal("open withdrawal released the goal's work")
	}
	// The withdrawn designation's terminal effect settles the action.
	next, err = next.RecordReceipt(2, ReceiptAccepted)
	if err != nil {
		t.Fatal(err)
	}
	next, err = next.Observe(Observation{Action: v.Action, Attempt: 2, Snapshot: s, Tick: 31, Effect: EffectUnsuccessful, UnsuccessfulReason: OutcomeNotAchieved, Causality: AfterDispatch}, s)
	if err != nil {
		t.Fatal(err)
	}
	if v = next.View(); v.Unresolved || v.Stage != Cancelled || GoalWorkOpen([]Progress{next}) {
		t.Fatalf("withdrawn attempt did not settle: %+v", v)
	}
	// A refused withdrawal resolves as absent like any unadmitted attempt.
	p, err = p.Withdraw(s, 30)
	if err != nil {
		t.Fatal(err)
	}
	if p, err = p.RecordReceipt(2, ReceiptRefused); err != nil || p.View().Unresolved {
		t.Fatal("refused withdrawal left the action open", err)
	}
}

func TestWithdrawRequiresCancelledPendingUnderCurrentAuthority(t *testing.T) {
	p, s := pendingCancelled(t)
	if _, err := p.Withdraw(s, 19); err == nil {
		t.Fatal("withdrawal accepted a rewound tick")
	}
	other := s
	other.Load = "other"
	if _, err := p.Withdraw(other, 30); err == nil {
		t.Fatal("withdrawal accepted another world")
	}
	live, s := dispatched(t)
	if _, err := live.Withdraw(s, 30); err == nil {
		t.Fatal("withdrawal accepted an uncancelled dispatch")
	}
	if live, err := live.Cancel(); err != nil {
		t.Fatal(err)
	} else if _, err = live.Withdraw(s, 30); err == nil {
		t.Fatal("withdrawal accepted a cancelled dispatch without a pending effect")
	}
}
