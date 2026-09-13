package domain

import (
	"reflect"
	"testing"
)

func TestHoldRecordsReasonsOnlyOnPendingOrPrepared(t *testing.T) {
	p, _ := fixture(t)
	held, err := p.Hold([]HeldReason{HeldUnsafeThreat, HeldCriticalMedical}, 5)
	if err != nil {
		t.Fatal(err)
	}
	reasons, ok := held.View().FreshHeldReason()
	if !ok || !reflect.DeepEqual(reasons, []HeldReason{HeldUnsafeThreat, HeldCriticalMedical}) {
		t.Fatal("hold reasons not surfaced", reasons, ok)
	}
	p, s := dispatched(t)
	if _, err := p.Hold([]HeldReason{HeldStaleFacts}, 10); err == nil {
		t.Fatal("held a dispatched (unresolved) action")
	}
	p, err = p.RecordReceipt(p.View().Attempt, ReceiptUnknown)
	if err != nil {
		t.Fatal(err)
	}
	p, err = p.Observe(Observation{Action: "a1", Attempt: 1, Snapshot: s, Tick: 11, Effect: EffectAbsent}, s)
	if err != nil {
		t.Fatal(err)
	}
	if p.View().Stage != Pending || p.View().Unresolved {
		t.Fatal("fixture setup broken", p.View())
	}
	if _, err := p.Hold([]HeldReason{HeldStaleFacts}, 11); err != nil {
		t.Fatal(err)
	}
}

func TestHoldRejectsInvalidOrStaleInput(t *testing.T) {
	p, _ := fixture(t)
	for _, reasons := range [][]HeldReason{nil, {}, {"bogus"}, {HeldUnsafeThreat, HeldUnsafeThreat}} {
		if got, err := p.Hold(reasons, 5); err == nil || !reflect.DeepEqual(got, p) {
			t.Fatal("invalid hold reasons accepted", reasons)
		}
	}
	prepared, err := p.Prepare(GenerationSnapshot{Colony: "colony", Load: "load", Plan: "p1", Revision: 1}, 10)
	if err != nil {
		t.Fatal(err)
	}
	if got, err := prepared.Hold([]HeldReason{HeldStaleFacts}, 5); err == nil || !reflect.DeepEqual(got, prepared) {
		t.Fatal("hold accepted a tick older than progress", err)
	}
}

// Every successful mutating transition must clear a previously recorded hold
// reason: this is the mechanism the exit evidence's "no stale action reasons"
// requirement relies on. A leftover reason must never survive Prepare,
// dispatch, receipt, observation, or cancellation.
func TestHoldClearsOnEverySuccessfulTransition(t *testing.T) {
	held := func(t *testing.T) (Progress, GenerationSnapshot) {
		t.Helper()
		p, s := fixture(t)
		p, err := p.Hold([]HeldReason{HeldUnknownFacts}, 0)
		if err != nil {
			t.Fatal(err)
		}
		if _, ok := p.View().FreshHeldReason(); !ok {
			t.Fatal("fixture did not record hold")
		}
		return p, s
	}
	t.Run("prepare", func(t *testing.T) {
		p, s := held(t)
		p, err := p.Prepare(s, 10)
		if err != nil {
			t.Fatal(err)
		}
		if _, ok := p.View().FreshHeldReason(); ok {
			t.Fatal("prepare left a stale hold reason")
		}
	})
	t.Run("dispatch", func(t *testing.T) {
		p, s := held(t)
		p, err := p.Prepare(s, 10)
		if err != nil {
			t.Fatal(err)
		}
		p, err = p.MarkDispatched(s, 10)
		if err != nil {
			t.Fatal(err)
		}
		if _, known := p.View().HeldReason.Value(); known {
			t.Fatal("dispatch left a stale hold reason")
		}
	})
	t.Run("receipt-refused-back-to-pending", func(t *testing.T) {
		p, s := held(t)
		p, err := p.Prepare(s, 10)
		if err != nil {
			t.Fatal(err)
		}
		p, err = p.MarkDispatched(s, 10)
		if err != nil {
			t.Fatal(err)
		}
		p, err = p.RecordReceipt(p.View().Attempt, ReceiptRefused)
		if err != nil {
			t.Fatal(err)
		}
		if p.View().Stage != Pending {
			t.Fatal("fixture setup broken", p.View())
		}
		if _, known := p.View().HeldReason.Value(); known {
			t.Fatal("refused receipt left a stale hold reason")
		}
	})
	t.Run("cancel", func(t *testing.T) {
		p, _ := held(t)
		p, err := p.Cancel()
		if err != nil {
			t.Fatal(err)
		}
		if _, known := p.View().HeldReason.Value(); known {
			t.Fatal("cancel left a stale hold reason")
		}
	})
}

// FreshHeldReason must reject evidence whose plan/revision no longer matches
// the view carrying it, even if the raw Fact were somehow never cleared -
// the defense-in-depth re-verification the exit evidence demands.
func TestFreshHeldReasonRejectsMismatchedEvidence(t *testing.T) {
	p, _ := fixture(t)
	p, err := p.Hold([]HeldReason{HeldStaleFacts}, 5)
	if err != nil {
		t.Fatal(err)
	}
	view := p.View()
	view.Revision++
	if _, ok := view.FreshHeldReason(); ok {
		t.Fatal("mismatched revision surfaced a hold reason")
	}
	view = p.View()
	view.Plan = "other-plan"
	if _, ok := view.FreshHeldReason(); ok {
		t.Fatal("mismatched plan surfaced a hold reason")
	}
	view = p.View()
	view.Stage = Dispatched
	if _, ok := view.FreshHeldReason(); ok {
		t.Fatal("dispatched stage surfaced a hold reason")
	}
	view = p.View()
	view.Unresolved = true
	if _, ok := view.FreshHeldReason(); ok {
		t.Fatal("unresolved view surfaced a hold reason")
	}
}
