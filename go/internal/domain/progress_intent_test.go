package domain

import "testing"

// bridge registers trade in production; domain tests stand in for it.
func init() { RegisterIntentKind(TradeAction) }

func dispatchedIntent(t *testing.T) (Progress, GenerationSnapshot) {
	t.Helper()
	value, err := NewTradeOpen("trader", "negotiator", false)
	if err != nil {
		t.Fatal(err)
	}
	a, err := NewTradeAction("a1", value)
	if err != nil {
		t.Fatal(err)
	}
	plan, err := NewPlan("p1", 1, []Action{a})
	if err != nil {
		t.Fatal(err)
	}
	p, err := NewProgress(plan, a.ID())
	if err != nil {
		t.Fatal(err)
	}
	s := GenerationSnapshot{Colony: "colony", Map: 0, Load: "load", Plan: "p1", Revision: 1}
	if p, err = p.Prepare(s, 10); err != nil {
		t.Fatal(err)
	}
	if p, err = p.MarkDispatched(s, 10); err != nil {
		t.Fatal(err)
	}
	return p, s
}

func TestIntentReceiptIsTerminal(t *testing.T) {
	if !TradeAction.IntentMode() || BuildingAction.IntentMode() {
		t.Fatal("intent mode is trade's alone")
	}
	cases := []struct {
		receipt Receipt
		stage   Stage
		effect  Effect
		known   bool
	}{
		{ReceiptAccepted, Completed, EffectCompleted, true},
		{ReceiptUnknown, Pending, "", false},
		{ReceiptRefused, Unsuccessful, EffectAbsent, true},
		{ReceiptUnsent, Pending, EffectAbsent, true},
	}
	for _, c := range cases {
		p, scope := dispatchedIntent(t)
		got, err := p.RecordReceipt(p.View().Attempt, c.receipt)
		if err != nil {
			t.Fatal(c.receipt, err)
		}
		v := got.View()
		effect, known := v.Effect.Value()
		if v.Unresolved || v.Stage != c.stage || known != c.known || effect != c.effect {
			t.Fatalf("%s: %+v", c.receipt, v)
		}
		if _, err := got.Observe(Observation{Action: v.Action, Attempt: v.Attempt, Snapshot: scope, Tick: 11, Effect: EffectCompleted, Causality: AfterDispatch}, scope); err == nil {
			t.Fatal(c.receipt, "an intent accepted an observation")
		}
		if c.stage == Pending {
			if _, err := got.Prepare(scope, 11); err != nil {
				t.Fatal(c.receipt, "a settled intent could not be dispatched again", err)
			}
		}
	}
}

func TestCancelledIntentReceiptStaysCancelled(t *testing.T) {
	p, _ := dispatchedIntent(t)
	p, err := p.Cancel()
	if err != nil {
		t.Fatal(err)
	}
	got, err := p.RecordReceipt(p.View().Attempt, ReceiptAccepted)
	if err != nil || got.View().Stage != Cancelled || got.View().Unresolved {
		t.Fatal(got.View(), err)
	}
}
