package domain

import "testing"

func TestDraftUnknownScopeSupersessionRetainsOrdinaryOutcome(t *testing.T) {
	for _, change := range []func(*GenerationSnapshot){func(s *GenerationSnapshot) { s.Colony = "replacement" }, func(s *GenerationSnapshot) { s.Map++ }, func(s *GenerationSnapshot) { s.Load = "replacement" }} {
		p, origin, _ := draftDispatched(t)
		var err error
		p, err = p.RecordDraftReceipt(1, ReceiptUnknown, Unknown[DraftClaim]())
		if err != nil {
			t.Fatal(err)
		}
		before := p.View()
		observed := origin
		change(&observed)
		observed.Native = 0
		observed.Direction = 0
		p, err = p.ObserveDraftScopeSupersession(DraftScopeSupersession{Action: before.Action, Attempt: 1, Origin: origin, Observed: observed, Tick: 0})
		if err != nil {
			t.Fatal(err)
		}
		cleanup, _ := p.View().DraftCleanup.Value()
		if cleanup.Stage != DraftSuperseded || cleanup.Claim.known || cleanup.Release.known {
			t.Fatal("invented claim or release", cleanup)
		}
		after := p.View()
		after.DraftCleanup = before.DraftCleanup
		if after != before {
			t.Fatal("ordinary progress changed")
		}
		if next, err := p.ObserveDraftScopeSupersession(DraftScopeSupersession{Action: before.Action, Attempt: 1, Origin: origin, Observed: observed, Tick: 1}); err == nil || next != p {
			t.Fatal("terminal cleanup accepted another transition")
		}
	}
}
func TestDraftScopeSupersessionRejectsUnprovenOrUnrelatedScope(t *testing.T) {
	p, origin, _ := draftDispatched(t)
	observed := origin
	observed.Load = "replacement"
	valid := DraftScopeSupersession{Action: p.View().Action, Attempt: 1, Origin: origin, Observed: observed, Tick: 0}
	for name, change := range map[string]func(*DraftScopeSupersession){
		"same world":               func(v *DraftScopeSupersession) { v.Observed = origin; v.Observed.Native = 0 },
		"different direction only": func(v *DraftScopeSupersession) { v.Observed = origin; v.Observed.Direction++ },
		"different action":         func(v *DraftScopeSupersession) { v.Action = "other" },
		"different attempt":        func(v *DraftScopeSupersession) { v.Attempt++ },
		"missing attempt":          func(v *DraftScopeSupersession) { v.Attempt = 0 },
		"different origin":         func(v *DraftScopeSupersession) { v.Origin.Native++ },
		"missing world":            func(v *DraftScopeSupersession) { v.Observed.Colony = "" },
		"negative tick":            func(v *DraftScopeSupersession) { v.Tick = -1 },
	} {
		t.Run(name, func(t *testing.T) {
			v := valid
			change(&v)
			if next, err := p.ObserveDraftScopeSupersession(v); err == nil || next != p {
				t.Fatal("invalid scope evidence accepted")
			}
		})
	}
	building, _ := dispatched(t)
	if next, err := building.ObserveDraftScopeSupersession(valid); err == nil || next != building {
		t.Fatal("building accepted draft supersession")
	}
}
func TestKnownClaimReplacementNeedsNoNewNativeAuthority(t *testing.T) {
	p, origin, claim := draftDispatched(t)
	p, err := p.RecordDraftReceipt(1, ReceiptAccepted, Known(claim))
	if err != nil {
		t.Fatal(err)
	}
	observed := origin
	observed.Load = "replacement"
	observed.Native = 0
	result, err := p.ObserveDraftCleanup(DraftCleanupObservation{Claim: claim, Observed: observed, Tick: 0, Outcome: DraftReleaseSuperseded})
	if err != nil {
		t.Fatal(err)
	}
	cleanup, _ := result.View().DraftCleanup.Value()
	if cleanup.Claim.value != claim || cleanup.Release.known || cleanup.Stage != DraftSuperseded {
		t.Fatal(cleanup)
	}
	observed = origin
	observed.Native = 0
	if next, err := p.ObserveDraftCleanup(DraftCleanupObservation{Claim: claim, Observed: observed, Tick: 11, Outcome: DraftReleaseSuperseded}); err == nil || next != p {
		t.Fatal("same-world missing native generation accepted")
	}
}
func TestScopeSupersessionRetainsKnownClaimAndReleaseRequest(t *testing.T) {
	p, origin, claim := draftDispatched(t)
	p, err := p.RecordDraftReceipt(1, ReceiptAccepted, Known(claim))
	if err != nil {
		t.Fatal(err)
	}
	p, err = p.BeginDraftCleanup(DraftReleaseRequest{Claim: claim, PawnSnapshotToken: "token", Observed: origin, Tick: 11})
	if err != nil {
		t.Fatal(err)
	}
	before, _ := p.View().DraftCleanup.Value()
	observed := origin
	observed.Map++
	p, err = p.ObserveDraftScopeSupersession(DraftScopeSupersession{Action: p.View().Action, Attempt: 1, Origin: origin, Observed: observed, Tick: 0})
	if err != nil {
		t.Fatal(err)
	}
	after, _ := p.View().DraftCleanup.Value()
	if after.Claim != before.Claim || after.Release != before.Release || after.Stage != DraftSuperseded {
		t.Fatal("lost original cleanup evidence")
	}
	release, _ := before.Release.Value()
	if next, err := p.RecordDraftCleanup(release, DraftReleaseUncertain); err == nil || next != p {
		t.Fatal("late release regressed supersession")
	}
}
