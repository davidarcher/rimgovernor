package executor

import (
	"context"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func emergencyFacts() policy.EmergencyFacts {
	return policy.EmergencyFacts{ColonistsComplete: domain.Known(true), ThreatsComplete: domain.Known(true)}
}
func emergencySnapshot(t *testing.T, current domain.GenerationSnapshot, tick domain.Tick, facts policy.EmergencyFacts) policy.EmergencySnapshot {
	t.Helper()
	snapshot, err := policy.NewEmergencySnapshot(current, tick, facts)
	if err != nil {
		t.Fatal(err)
	}
	return snapshot
}
func emergencyDanger() policy.EmergencyFacts {
	facts := emergencyFacts()
	for _, id := range []policy.PawnID{"hostile-one", "hostile-two"} {
		facts.Threats = append(facts.Threats, policy.EmergencyThreat{ID: id, Kind: policy.Hostile, Dead: domain.Known(false), Downed: domain.Known(false)})
	}
	for _, id := range []policy.PawnID{"patient-one", "patient-two"} {
		facts.Colonists = append(facts.Colonists, policy.EmergencyPawn{ID: id, Dead: domain.Known(false), Downed: domain.Known(true), Bleeding: domain.Known(true), NeedsTend: domain.Known(true)})
	}
	return facts
}
func TestEmergencyGatePreservesPreparedReservation(t *testing.T) {
	for _, dangerAt := range []int{1, 2} {
		t.Run(map[int]string{1: "before-prepare", 2: "before-dispatch"}[dangerAt], func(t *testing.T) {
			f := newFixture(t)
			f.env.onInspect = func(n int, i Inspection) Inspection {
				if n == dangerAt {
					i.Emergency = emergencySnapshot(t, i.Current, i.Tick, emergencyDanger())
				}
				return i
			}
			result, err := f.run()
			if !errors.Is(err, ErrHeld) || result.NativeCalled || len(result.Refused) != 2 {
				t.Fatal(result, err)
			}
			reasons := map[policy.Reason]bool{}
			for _, refusal := range result.Refused {
				if refusal.Action != f.action.ID() || refusal.Resource != "" || reasons[refusal.Reason] {
					t.Fatal("incorrect emergency refusal", refusal)
				}
				reasons[refusal.Reason] = true
			}
			if !reasons[policy.CriticalMedical] || !reasons[policy.UnsafeThreat] {
				t.Fatal(reasons)
			}
			state, err := f.store.LoadPlan(context.Background(), f.plan.ID())
			if err != nil {
				t.Fatal(err)
			}
			if dangerAt == 1 {
				if state.Progress[0].View().Stage != domain.Pending || len(state.Admissions) != 0 {
					t.Fatal("unsafe work prepared", state)
				}
			} else {
				if state.Progress[0].View().Stage != domain.Prepared || len(state.Admissions) != 1 || len(state.Admissions[0].Admission.Costs) != 1 || len(state.Admissions[0].Admission.Footprint) != 1 {
					t.Fatal("hold erased prepared reservation", state)
				}
			}
			if state.Progress[0].View().Attempt != 0 || state.Progress[0].View().Unresolved {
				t.Fatal("hold dispatched", state.Progress[0].View())
			}
			held, ok := state.Progress[0].View().FreshHeldReason()
			if !ok {
				t.Fatal("emergency hold was not persisted as a held reason")
			}
			gotReasons := map[domain.HeldReason]bool{}
			for _, r := range held {
				gotReasons[r] = true
			}
			if !gotReasons[domain.HeldCriticalMedical] || !gotReasons[domain.HeldUnsafeThreat] || len(gotReasons) != 2 {
				t.Fatal("wrong persisted hold reasons", held)
			}
			if _, ok := result.Progress.View().FreshHeldReason(); !ok {
				t.Fatal("Run result did not carry the persisted hold reason")
			}
			if inspections, placements, _ := f.env.counts(); inspections != dangerAt || placements != 0 {
				t.Fatal(inspections, placements)
			}
		})
	}
}
func TestEmergencyMissingStaleAndUnknownFactsHold(t *testing.T) {
	for name, edit := range map[string]func(*testing.T, Inspection) policy.EmergencySnapshot{
		"missing": func(*testing.T, Inspection) policy.EmergencySnapshot { return policy.EmergencySnapshot{} },
		"unknown-health": func(t *testing.T, i Inspection) policy.EmergencySnapshot {
			facts := emergencyFacts()
			facts.Colonists = []policy.EmergencyPawn{{ID: "patient", Dead: domain.Known(false), Downed: domain.Known(false)}}
			return emergencySnapshot(t, i.Current, i.Tick, facts)
		},
		"incomplete": func(t *testing.T, i Inspection) policy.EmergencySnapshot {
			facts := emergencyFacts()
			facts.ThreatsComplete = domain.Unknown[bool]()
			return emergencySnapshot(t, i.Current, i.Tick, facts)
		},
		"older-tick": func(t *testing.T, i Inspection) policy.EmergencySnapshot {
			return emergencySnapshot(t, i.Current, i.Tick-1, emergencyFacts())
		},
		"different-generation": func(t *testing.T, i Inspection) policy.EmergencySnapshot {
			g := i.Current
			g.Native++
			return emergencySnapshot(t, g, i.Tick, emergencyFacts())
		},
		"different-load": func(t *testing.T, i Inspection) policy.EmergencySnapshot {
			g := i.Current
			g.Load = "new-load"
			return emergencySnapshot(t, g, i.Tick, emergencyFacts())
		},
	} {
		t.Run(name, func(t *testing.T) {
			f := newFixture(t)
			f.env.onInspect = func(_ int, i Inspection) Inspection { i.Emergency = edit(t, i); return i }
			result, err := f.run()
			if !errors.Is(err, ErrHeld) || result.NativeCalled || len(result.Refused) != 1 {
				t.Fatal(result, err)
			}
			if f.progress(t).Stage != domain.Pending {
				t.Fatal("held facts prepared")
			}
		})
	}
}
func TestEmergencyPreparedRestartRequiresFreshClearance(t *testing.T) {
	f := newFixture(t)
	f.env.onInspect = func(n int, i Inspection) Inspection {
		if n == 2 {
			i.Emergency = emergencySnapshot(t, i.Current, i.Tick, emergencyDanger())
		}
		return i
	}
	if _, err := f.run(); !errors.Is(err, ErrHeld) {
		t.Fatal(err)
	}
	if err := f.store.Close(); err != nil {
		t.Fatal(err)
	}
	reopened, err := store.Open(context.Background(), f.path)
	if err != nil {
		t.Fatal(err)
	}
	defer reopened.Close()
	f.store = reopened
	f.executor, err = New(reopened, f.env, f.clock, f.executor.limits)
	if err != nil {
		t.Fatal(err)
	}
	if err = f.executor.UpdateAuthority(f.authority); err != nil {
		t.Fatal(err)
	}
	f.env.onInspect = func(_ int, i Inspection) Inspection { i.Emergency = policy.EmergencySnapshot{}; return i }
	if result, err := f.run(); !errors.Is(err, ErrHeld) || result.NativeCalled || f.progress(t).Stage != domain.Prepared {
		t.Fatal(result, err)
	}
	// A stale/missing emergency snapshot is itself a held reason (StaleFacts),
	// so it must still be freshly reported at the current tick/revision.
	if _, ok := f.progress(t).FreshHeldReason(); !ok {
		t.Fatal("missing emergency snapshot was not reported as a held reason")
	}
	f.env.onInspect = nil
	if result, err := f.run(); err != nil || !result.NativeCalled || !result.Progress.View().Unresolved {
		t.Fatal(result, err)
	}
	// Once dispatch succeeds, the earlier hold reason must never be served again.
	if _, ok := f.progress(t).FreshHeldReason(); ok {
		t.Fatal("resolved dispatch still served a stale hold reason")
	}
}
func TestEmergencyDoesNotBlockIssuedAttemptReconciliation(t *testing.T) {
	f := newFixture(t)
	f.env.onPlace = func(context.Context, Placement) (Receipt, error) { return Receipt{}, errors.New("lost reply") }
	if result, err := f.run(); err == nil || !result.Progress.View().Unresolved {
		t.Fatal(result, err)
	}
	before, _, _ := f.env.counts()
	f.env.onInspect = func(_ int, i Inspection) Inspection {
		t.Error("issued attempt inspected routine admission")
		i.Emergency = emergencySnapshot(t, i.Current, i.Tick, emergencyDanger())
		return i
	}
	f.env.onObserve = func(p Placement, g domain.GenerationSnapshot, _ int) Evidence {
		return f.env.evidence(p, g, domain.EffectCompleted, true)
	}
	result, err := f.run()
	if err != nil || result.NativeCalled || result.Progress.View().Stage != domain.Completed {
		t.Fatal(result, err)
	}
	if inspections, placements, observations := f.env.counts(); inspections != before || placements != 1 || observations != 1 {
		t.Fatal(inspections, placements, observations)
	}
}
