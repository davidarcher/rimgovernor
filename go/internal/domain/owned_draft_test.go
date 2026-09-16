package domain

import (
	"strings"
	"testing"
)

func draftDispatched(t *testing.T) (Progress, GenerationSnapshot, DraftClaim) {
	t.Helper()
	intent, err := NewOwnedDraft("pawn")
	if err != nil {
		t.Fatal(err)
	}
	action, err := NewOwnedDraftAction("draft", intent)
	if err != nil {
		t.Fatal(err)
	}
	plan, err := NewPlan("plan", 1, []Action{action})
	if err != nil {
		t.Fatal(err)
	}
	p, err := NewProgress(plan, action.ID())
	if err != nil {
		t.Fatal(err)
	}
	s := GenerationSnapshot{Colony: "colony", Map: 0, Load: "load", Plan: "plan", Revision: 1, Native: 1}
	p, err = p.Prepare(s, 10)
	if err != nil {
		t.Fatal(err)
	}
	p, err = p.MarkDispatched(s, 10)
	if err != nil {
		t.Fatal(err)
	}
	return p, s, DraftClaim{Action: action.ID(), Attempt: 1, Pawn: "pawn", Claim: "claim", Session: "session", Origin: s}
}
func draftObservation(p Progress, s GenerationSnapshot, e Effect) Observation {
	return Observation{Action: p.View().Action, Attempt: p.View().Attempt, Snapshot: s, Tick: 11, Effect: e, Causality: AfterDispatch}
}
func TestOwnedDraftFlatIntentAndCoverage(t *testing.T) {
	for _, id := range []string{"", " ", "a\x00b", strings.Repeat("x", 257)} {
		if _, err := NewOwnedDraft(PawnID(id)); err == nil {
			t.Fatal("invalid pawn")
		}
	}
	d, _ := NewOwnedDraft("pawn")
	a, _ := NewOwnedDraftAction("draft", d)
	got, ok := a.OwnedDraft()
	if !ok || got != d || got.Pawn() != "pawn" {
		t.Fatal(a)
	}
	if _, ok = a.Building(); ok {
		t.Fatal("mixed family")
	}
	if _, err := NewPlan("plan", 1, []Action{a, a}); err == nil {
		t.Fatal("duplicate actions")
	}
	if ValidateHandlerCoverage([]ActionKind{BuildingAction}) == nil || ValidateHandlerCoverage(SupportedActionKinds()) != nil {
		t.Fatal("draft handler not covered")
	}
	p, _, _ := draftDispatched(t)
	_ = map[ProgressView]bool{p.View(): true}
	_ = map[Action]bool{a: true}
	if c, _ := p.View().DraftCleanup.Value(); c.Stage != DraftAwaitingClaim {
		t.Fatal(c)
	}
}
func TestDraftRequiresAtomicFamilyTransitions(t *testing.T) {
	p, s, claim := draftDispatched(t)
	if next, err := p.RecordReceipt(1, ReceiptRefused); err == nil || next != p {
		t.Fatal("generic receipt bypass")
	}
	if next, err := p.Observe(draftObservation(p, s, EffectAbsent), s); err == nil || next != p {
		t.Fatal("generic observation bypass")
	}
	for _, change := range []func(*DraftClaim){func(c *DraftClaim) { c.Pawn = "other" }, func(c *DraftClaim) { c.Attempt = 2 }, func(c *DraftClaim) { c.Session = "" }, func(c *DraftClaim) { c.Claim = "" }} {
		bad := claim
		change(&bad)
		if next, err := p.RecordDraftReceipt(1, ReceiptAccepted, Known(bad)); err == nil || next != p {
			t.Fatal("bad claim mutated progress")
		}
	}
	if _, err := p.RecordDraftReceipt(1, ReceiptRefused, Known(claim)); err == nil {
		t.Fatal("refusal with claim")
	}
}
func TestDraftCompletedAndCancelledKeepCleanup(t *testing.T) {
	for _, cancelled := range []bool{false, true} {
		p, s, claim := draftDispatched(t)
		var err error
		if cancelled {
			p, err = p.Cancel()
			if err != nil {
				t.Fatal(err)
			}
		}
		p, err = p.RecordDraftReceipt(1, ReceiptUnknown, Known(claim))
		if err != nil {
			t.Fatal(err)
		}
		p, err = p.ObserveDraft(draftObservation(p, s, EffectCompleted), s, Known(claim))
		if err != nil {
			t.Fatal(err)
		}
		c, _ := p.View().DraftCleanup.Value()
		if p.View().Unresolved || c.Stage != DraftCleanupRequired || !p.draftCleanupOutstanding() {
			t.Fatal(p)
		}
		if cancelled && p.View().Stage != Cancelled || !cancelled && p.View().Stage != Completed {
			t.Fatal(p)
		}
		newer := s
		newer.Native = 2
		request := DraftReleaseRequest{Claim: claim, PawnSnapshotToken: "fresh", Observed: newer, Tick: 12}
		p, err = p.BeginDraftCleanup(request)
		if err != nil {
			t.Fatal(err)
		}
		c, _ = p.View().DraftCleanup.Value()
		release, _ := c.Release.Value()
		if release.Sequence != 1 || release.Request != request {
			t.Fatal(release)
		}
		p, err = p.RecordDraftCleanup(release, DraftReleaseConfirmed)
		if err != nil {
			t.Fatal(err)
		}
		if p.draftCleanupOutstanding() {
			t.Fatal("released still outstanding")
		}
		if next, err := p.RecordDraftCleanup(release, DraftReleaseUncertain); err == nil || next != p {
			t.Fatal("terminal cleanup regressed")
		}
	}
}
func TestDraftNotAcquiredRequiresProofAndRetainsAttemptIdentity(t *testing.T) {
	p, s, _ := draftDispatched(t)
	p, err := p.RecordDraftReceipt(1, ReceiptUnknown, Unknown[DraftClaim]())
	if err != nil {
		t.Fatal(err)
	}
	if !p.draftCleanupOutstanding() {
		t.Fatal("unknown receipt erased obligation")
	}
	p, err = p.ObserveDraft(draftObservation(p, s, EffectAbsent), s, Unknown[DraftClaim]())
	if err != nil {
		t.Fatal(err)
	}
	c, _ := p.View().DraftCleanup.Value()
	if c.Stage != DraftNotAcquired {
		t.Fatal(c)
	}
	p, err = p.Prepare(s, 12)
	if err != nil {
		t.Fatal(err)
	}
	p, err = p.MarkDispatched(s, 12)
	if err != nil {
		t.Fatal(err)
	}
	if p.View().Attempt != 2 {
		t.Fatal(p)
	}
	if next, err := p.RecordDraftReceipt(1, ReceiptUnknown, Unknown[DraftClaim]()); err == nil || next != p {
		t.Fatal("old receipt accepted")
	}
	refused, _, _ := draftDispatched(t)
	refused, err = refused.RecordDraftReceipt(1, ReceiptRefused, Unknown[DraftClaim]())
	if err != nil {
		t.Fatal(err)
	}
	c, _ = refused.View().DraftCleanup.Value()
	if c.Stage != DraftNotAcquired {
		t.Fatal(c)
	}
}
func TestDraftUnsuccessfulCanResolveLateClaimWithoutReactivation(t *testing.T) {
	for _, acquired := range []bool{false, true} {
		p, s, claim := draftDispatched(t)
		observation := draftObservation(p, s, EffectUnsuccessful)
		observation.UnsuccessfulReason = NativeInterrupted
		p, err := p.ObserveDraft(observation, s, Unknown[DraftClaim]())
		if err != nil {
			t.Fatal(err)
		}
		if !p.draftCleanupOutstanding() {
			t.Fatal("unsuccessful inferred no claim")
		}
		evidence := Unknown[DraftClaim]()
		effect := EffectAbsent
		if acquired {
			evidence = Known(claim)
			effect = EffectUnknown
		}
		observation = draftObservation(p, s, effect)
		observation.Tick = 12
		p, err = p.ObserveDraft(observation, s, evidence)
		if err != nil {
			t.Fatal(err)
		}
		if p.View().Stage != Unsuccessful || p.View().Unresolved {
			t.Fatal("reactivated action")
		}
		c, _ := p.View().DraftCleanup.Value()
		if acquired && c.Stage != DraftCleanupRequired || !acquired && c.Stage != DraftNotAcquired {
			t.Fatal(c)
		}
	}
}
func TestDraftReleaseImmutableRequestSequenceAndOverflow(t *testing.T) {
	p, s, claim := draftDispatched(t)
	p, err := p.RecordDraftReceipt(1, ReceiptAccepted, Known(claim))
	if err != nil {
		t.Fatal(err)
	}
	request := DraftReleaseRequest{Claim: claim, PawnSnapshotToken: "token", Observed: s, Tick: 11}
	p, err = p.BeginDraftCleanup(request)
	if err != nil {
		t.Fatal(err)
	}
	c, _ := p.View().DraftCleanup.Value()
	first, _ := c.Release.Value()
	changed := first
	changed.Request.PawnSnapshotToken = "different"
	if next, err := p.RecordDraftCleanup(changed, DraftReleaseConfirmed); err == nil || next != p {
		t.Fatal("changed request accepted")
	}
	p, err = p.RecordDraftCleanup(first, DraftReleaseUncertain)
	if err != nil {
		t.Fatal(err)
	}
	request.Tick = 12
	p, err = p.BeginDraftCleanup(request)
	if err != nil {
		t.Fatal(err)
	}
	c, _ = p.View().DraftCleanup.Value()
	second, _ := c.Release.Value()
	if second.Sequence != 2 || p.View().Attempt != 1 {
		t.Fatal(second)
	}
	if next, err := p.RecordDraftCleanup(first, DraftReleaseConfirmed); err == nil || next != p {
		t.Fatal("old cleanup reply accepted")
	}
	p, err = p.RecordDraftCleanup(second, DraftReleaseUncertain)
	if err != nil {
		t.Fatal(err)
	}
	c, _ = p.View().DraftCleanup.Value()
	second.Sequence = ^uint64(0)
	c.Release = Known(second)
	p.view.DraftCleanup = Known(c)
	if next, err := p.BeginDraftCleanup(request); err == nil || next != p {
		t.Fatal("cleanup sequence wrapped")
	}
}
func TestDraftClaimBlocksRetryAndCannotBeReplaced(t *testing.T) {
	p, s, claim := draftDispatched(t)
	p, err := p.RecordDraftReceipt(1, ReceiptAccepted, Known(claim))
	if err != nil {
		t.Fatal(err)
	}
	p, err = p.ObserveDraft(draftObservation(p, s, EffectAbsent), s, Unknown[DraftClaim]())
	if err != nil {
		t.Fatal(err)
	}
	if next, err := p.Prepare(s, 12); err == nil || next != p {
		t.Fatal("retry lost existing claim")
	}
	changed := claim
	changed.Claim = "replacement"
	if _, err := p.bindDraftClaim(Known(changed)); err == nil {
		t.Fatal("claim replaced")
	}
}
func TestDraftPositiveSupersessionNeedsNoRelease(t *testing.T) {
	for _, worldChanged := range []bool{false, true} {
		p, s, claim := draftDispatched(t)
		p, err := p.RecordDraftReceipt(1, ReceiptAccepted, Known(claim))
		if err != nil {
			t.Fatal(err)
		}
		tick := Tick(11)
		if worldChanged {
			s.Load = "replacement"
			tick = 0
		}
		p, err = p.ObserveDraftCleanup(DraftCleanupObservation{Claim: claim, Observed: s, Tick: tick, Outcome: DraftReleaseSuperseded})
		if err != nil {
			t.Fatal(err)
		}
		c, _ := p.View().DraftCleanup.Value()
		if c.Stage != DraftSuperseded || c.Release.known || c.Claim.value != claim {
			t.Fatal("supersession fabricated dispatch", c)
		}
	}
	p, s, claim := draftDispatched(t)
	p, _ = p.RecordDraftReceipt(1, ReceiptAccepted, Known(claim))
	for _, outcome := range []DraftCleanupOutcome{DraftReleaseConfirmed, DraftReleaseUncertain} {
		if next, err := p.ObserveDraftCleanup(DraftCleanupObservation{Claim: claim, Observed: s, Tick: 11, Outcome: outcome}); err == nil || next != p {
			t.Fatal("observation fabricated release")
		}
	}
}
