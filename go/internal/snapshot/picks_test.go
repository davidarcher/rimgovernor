package snapshot

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Planner picks converted from the native upkeep/* and clearance/* cases
// (#746): each planner step's recorded policy inputs, the reads the
// rounds's facts do not carry (feed benches and zones, haulers,
// dump sites, shrine squads and breach readiness), replayed through the
// policy that chose the bench, cell, pawn or casket.

func planner(t *testing.T, path string) Planner {
	t.Helper()
	p, err := LoadPlanner(path)
	if err != nil {
		t.Fatal(err)
	}
	return p
}

// homeShrineCaskets replays the rounds's open targets for its one
// Home shrine.
func homeShrineCaskets(t *testing.T, path string) (policy.AncientShrine, []policy.ShrineCasket) {
	t.Helper()
	row, p := homeShrine(t, path)
	caskets := policy.ShrineOpenTargets([]policy.AncientShrine{row}, p)[row.ID]
	if len(caskets) != 2 {
		t.Fatalf("%s: open targets %v, want two", path, caskets)
	}
	return row, caskets
}

// clearance/shrine-open, tick 15: the melee lock stands a squad colonist
// at each filled casket and the lowest casket's locker opens it.
func TestPickShrineMeleeLock(t *testing.T) {
	_, caskets := homeShrineCaskets(t, "testdata/planner-shrine-open-routine.json.gz")
	squad := planner(t, "testdata/planner-shrine-open.json").ShrineSquads
	if len(squad) != 1 {
		t.Fatalf("%d squad reads", len(squad))
	}
	lock := policy.ShrineMeleeLock(caskets, squad[0])
	want := map[string]domain.PawnID{"Thing_AncientCryptosleepCasket44710": "Thing_Human724", "Thing_AncientCryptosleepCasket44718": "Thing_Human726"}
	if lock.Reason != "" || !reflect.DeepEqual(lock.Lockers, want) || lock.Opener != "Thing_Human724" || lock.Casket != "Thing_AncientCryptosleepCasket44710" {
		t.Errorf("lock %+v", lock)
	}
}

// clearance/shrine-claim, tick 15: the sealed shrine is ready to breach
// through Thing_Wall44693 with the full eight-colonist squad, the three
// colony traps accounted for.
func TestPickShrineBreachReadiness(t *testing.T) {
	p := planner(t, "testdata/planner-shrine-breach-ready.json")
	if len(p.ShrineReadiness) != 1 {
		t.Fatalf("%d readiness requests", len(p.ShrineReadiness))
	}
	r := policy.ShrineBreachReadiness(p.ShrineReadiness[0])
	if !r.Ready || r.Wall.EntityID != "Thing_Wall44693" || len(r.Squad) != 8 || r.Traps != 3 {
		t.Errorf("readiness %+v", r)
	}
}

// clearance/salvage-hold-resume, tick 26: the recovery queue works the remote
// battery ruin, holds it threat_present while a raider stands beside it, and
// works it again once the raider is gone.
func TestPickSalvageHoldAndResume(t *testing.T) {
	const ruin = "Thing_Battery12682"
	for _, step := range []struct {
		file, hold string
	}{
		{"salvage-hold-selected", ""},
		{"salvage-hold-threat", policy.RemoteHoldThreat},
		{"salvage-hold-resumed", ""},
	} {
		_, _, q := recovery(t, "testdata/"+step.file+".json.gz")
		e, ok := entry(q, ruin)
		if !ok || e.Reason != step.hold || (step.hold == "") != working(q, ruin) {
			t.Errorf("%s: %+v, want hold %q", step.file, e, step.hold)
		}
	}
}
