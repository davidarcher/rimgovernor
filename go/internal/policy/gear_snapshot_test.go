package policy_test

import (
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/snapshot"
)

func loadRoutine(t *testing.T, path string) snapshot.Routine {
	t.Helper()
	r, err := snapshot.Load(path)
	if err != nil {
		t.Fatal(err)
	}
	return r
}

func recordedGear(t *testing.T, r snapshot.Routine) policy.GearObservation {
	t.Helper()
	obs, known := r.Facts.Gear.Value()
	if !known || len(obs.Pawns) == 0 {
		t.Fatalf("tick %d: gear census unknown", r.Tick)
	}
	return obs
}

func wantEquipmentDeficit(t *testing.T, r snapshot.Routine) {
	t.Helper()
	a, err := r.Assessment(policy.MaintainEquipment)
	if err != nil {
		t.Fatal(err)
	}
	if a.Need != domain.NeedDeficit {
		t.Fatalf("tick %d: MaintainEquipment %s, want deficit", r.Tick, a.Need)
	}
}

// Replaces the native gear/tainted case (#468, #748): the tribal baseline
// staged by test/gear_area_prepare mode "tainted", an excellent tainted
// parka (Thing_Apparel_Parka44690) beside a clean one (44691), every
// colonist on the scenario's Anything policy. Recorded from `acceptance
// run gear/tainted` at 4e86b4663 (serve families work,gear):
// gear-tainted-anything-policy.json is the first review (tick 15),
// gear-tainted-worker-policy.json the review at tick 54369 after the
// apparel-policy writes landed. (That run failed the case's recovery
// window on an unrelated cold replacement; the policy decisions below are
// what it asserted about tainted apparel.)
const taintedParka = "Thing_Apparel_Parka44690"

func TestGearTaintedAssignsTheWorkerPolicy(t *testing.T) {
	r := loadRoutine(t, "testdata/gear-tainted-anything-policy.json")
	wantEquipmentDeficit(t, r)
	offered := false
	for _, p := range recordedGear(t, r).Pawns {
		p = named(p, "Name "+string(p.Pawn))
		desired, needed := policy.DesiredApparelPolicy(p)
		if !needed || desired.Spec().Name != "Name "+string(p.Pawn) {
			t.Fatalf("%s: policy write %v %q, want the pawn's own outfit", p.Pawn, needed, desired.Spec().Name)
		}
		candidates, _ := p.Candidates.Value()
		for _, c := range candidates {
			offered = offered || c.Target == taintedParka
		}
	}
	// Under Anything the tainted parka is still offered: the policy write
	// is what keeps it off.
	if !offered {
		t.Fatalf("the Anything recording offers no tainted parka; re-record")
	}
}

func TestGearTaintedWorkerPolicyNeverWearsTheTaintedParka(t *testing.T) {
	r := loadRoutine(t, "testdata/gear-tainted-worker-policy.json")
	obs := recordedGear(t, r)
	for _, p := range obs.Pawns {
		state, known := p.Policy.Value()
		// The recording predates per-pawn outfits (#1302): its worker policy
		// stands in for the pawn's own.
		p = named(p, state.Current.Name)
		if !known || state.Current.Name != "RimGovernor "+string(policy.GearWorker) || !state.ExcludesTainted {
			t.Fatalf("%s: on %q excludesTainted=%v", p.Pawn, state.Current.Name, state.ExcludesTainted)
		}
		if _, needed := policy.DesiredApparelPolicy(p); needed {
			t.Fatalf("%s: another policy write proposed on the worker policy", p.Pawn)
		}
		candidates, _ := p.Candidates.Value()
		for _, c := range candidates {
			if c.Target == taintedParka {
				t.Fatalf("%s: tainted parka still a candidate", p.Pawn)
			}
		}
	}
	m, err := policy.SelectGearMethod(policy.GearPlanningRequest{Observation: r.Facts.Gear, Stock: candidateStock(obs)})
	if err != nil {
		t.Fatal(err)
	}
	if m.Kind != policy.GearReplace || m.Target == taintedParka {
		t.Fatalf("method %+v, want a wear order for the clean parka", m)
	}
}

// Replaces the native gear/winter case (#467, #748): the tribal baseline
// moved to one day before its first winter twelfth (winter opens at tick
// 60015), ComplexClothing finished, cloth and a hand tailoring bench
// supplied, nobody owning winter wear (test/gear_area_prepare mode
// "winter"). gear-winter-autumn-lookahead.json is the review at tick 10043
// of `acceptance run gear/winter` at 4e86b4663 (serve families
// work,workshop,gear). Before winter opens the season lookahead already
// holds MaintainEquipment in deficit and asks every colonist for a cloth
// parka or jacket and a cloth tuque. (That run's window closed before any
// bill finished; the bench bill needs the workshop census the routine
// snapshot does not carry.)
func TestGearWinterLookaheadAsksForWinterWearBeforeWinter(t *testing.T) {
	const winterTick = 60015
	r := loadRoutine(t, "testdata/gear-winter-autumn-lookahead.json")
	if r.Tick >= winterTick {
		t.Fatalf("recording at tick %d is not before winter", r.Tick)
	}
	wantEquipmentDeficit(t, r)
	for _, p := range recordedGear(t, r).Pawns {
		needs, known := p.Replacements.Value()
		if !known {
			t.Fatalf("%s: replacements unknown", p.Pawn)
		}
		outer, head := false, false
		for _, n := range needs {
			if n.Reason != "cold" || n.Stuff != "Cloth" {
				continue
			}
			outer = outer || n.Definition == "Apparel_Parka" || n.Definition == "Apparel_Jacket"
			head = head || n.Definition == "Apparel_Tuque"
		}
		if !outer || !head {
			t.Fatalf("%s: lookahead asks for no cloth parka/jacket (%v) or tuque (%v): %+v", p.Pawn, outer, head, needs)
		}
	}
}

// Replaces the weapon half of the native gear/soldier case (#471, #748):
// two unarmed soldiers, Thing_Human724 at Shooting 12 and Thing_Human726
// at Shooting 8 (test/gear_area_prepare mode "soldier" on the 11x11
// starter site), with a bolt-action rifle and a pump shotgun loose. The
// equip step's weapon assignment reads outside the routine review, so its
// AssignEquip input was recorded by a temporary dump at the policy call
// (buildingruntime RoutineEquipPlanner) during `acceptance run
// gear/soldier` at 4e86b4663. In that run both soldiers were downed when
// the equip step first planned (the case failed: nobody ended armed or
// armored), so the recording hands the guns to others; the test stands
// the soldiers back up and asserts the fit the case asserted. The armor
// half is snapshot.TestGearSoldierDraftedPlansFlakVestAndHelmet (#979).
func TestGearSoldierWeaponFitBySkill(t *testing.T) {
	var in struct {
		Pawns       []policy.EquipCandidatePawn
		Weapons     []policy.EquipCandidateWeapon
		Assignments []policy.EquipAssignment
	}
	loadRecorded(t, "testdata/gear-soldier-equip.json", &in)
	const marksman, second = "Thing_Human724", "Thing_Human726"
	for i := range in.Pawns {
		if p := &in.Pawns[i]; p.Pawn == marksman || p.Pawn == second {
			if downed, _ := p.Downed.Value(); !downed {
				t.Fatalf("%s is not downed in the recording; drop the variant", p.Pawn)
			}
			p.Downed = domain.Known(false)
		}
	}
	held := map[domain.PawnID]string{}
	for _, a := range policy.AssignEquip(in.Pawns, in.Weapons) {
		held[a.Pawn] = a.Weapon.Definition
	}
	if held[marksman] != "Gun_BoltActionRifle" || held[second] != "Gun_PumpShotgun" {
		t.Fatalf("Shooting 12 gets %q and Shooting 8 gets %q; want the rifle then the shotgun", held[marksman], held[second])
	}
}

// candidateStock stands in for the supply-stock read the step makes for
// every candidate definition (not part of the routine snapshot): each
// counts the distinct stored items the census offers.
func candidateStock(obs policy.GearObservation) []policy.Stock {
	items := map[policy.Resource]map[string]bool{}
	for _, p := range obs.Pawns {
		candidates, _ := p.Candidates.Value()
		for _, c := range candidates {
			if items[c.Definition] == nil {
				items[c.Definition] = map[string]bool{}
			}
			items[c.Definition][c.Target] = true
		}
	}
	var stock []policy.Stock
	for def, targets := range items {
		stock = append(stock, policy.Stock{Resource: def, Available: domain.Known(int64(len(targets)))})
	}
	return stock
}

// wearRound replays one development round of RoutineGearPlanner.stepOne
// over a recorded census: each admitted wear order blocks its pawn and
// claims its target for the rest of the round, until the planner stops
// choosing wear orders. It returns the orders and the method that ended
// the round.
func wearRound(t *testing.T, obs policy.GearObservation, stock []policy.Stock) ([]policy.GearMethod, policy.GearMethod) {
	t.Helper()
	var orders []policy.GearMethod
	var seen []domain.MethodID
	for range 256 {
		m, err := policy.SelectGearMethod(policy.GearPlanningRequest{Observation: domain.Known(obs), Seen: seen, Stock: stock})
		if err != nil {
			t.Fatal(err)
		}
		if m.Kind != policy.GearReplace {
			return orders, m
		}
		orders, seen = append(orders, m), append(seen, m.ID)
		for i := range obs.Pawns {
			p := &obs.Pawns[i]
			p.Blocked = p.Blocked || p.Pawn == m.Pawn
			candidates, _ := p.Candidates.Value()
			var left []policy.GearCandidate
			for _, c := range candidates {
				if c.Target != m.Target {
					left = append(left, c)
				}
			}
			p.Candidates = domain.Known(left)
		}
	}
	t.Fatal("wear round never ended")
	return nil, policy.GearMethod{}
}

// Replaces the native gear/roster case (#469, #748): the baseline grown to
// twelve colonists stripped of shirt and headgear, a stockpile holding a
// shirt and a tuque per pawn plus spares (test/gear_area_prepare mode
// "roster"). gear-roster-stripped.json.gz is the first review (tick 15) of
// `acceptance run gear/roster`, re-recorded for #769 once the native census
// offered every eligible item instead of each pawn's best eight.
// Every pawn is offered a stored shirt and tuque, and two planner rounds
// (one wear per pawn per round; the census refreshes between them) dress
// every pawn in both from storage, each stored item once, before any bill.
func TestGearRosterDressesEveryPawnFromStorage(t *testing.T) {
	r := loadRoutine(t, "testdata/gear-roster-stripped.json.gz")
	wantEquipmentDeficit(t, r)
	obs := recordedGear(t, r)
	if len(obs.Pawns) != 12 {
		t.Fatalf("recorded %d colonists, want 12", len(obs.Pawns))
	}
	for _, p := range obs.Pawns {
		candidates, _ := p.Candidates.Value()
		if !hasDefinition(candidates, rosterShirt) || !hasDefinition(candidates, rosterTuque) {
			t.Fatalf("%s is not offered both a stored shirt and a tuque", p.Pawn)
		}
	}
	stock := candidateStock(obs)
	worn := map[policy.PawnID]map[policy.Resource]bool{}
	targets := map[string]bool{}
	for round := range 2 {
		orders, end := wearRound(t, obs, stock)
		if end.Kind == policy.GearReplace || end.Kind == policy.GearProduce {
			t.Fatalf("round %d ended on %+v before the bench census", round, end)
		}
		for _, m := range orders {
			if targets[m.Target] {
				t.Fatalf("order %+v wears a stored item twice", m)
			}
			targets[m.Target] = true
			def := policy.Resource(rosterShirt)
			if strings.Contains(m.Target, rosterTuque) {
				def = rosterTuque
			} else if !strings.Contains(m.Target, rosterShirt) {
				t.Fatalf("order %+v is not a stored shirt or tuque", m)
			}
			if worn[m.Pawn] == nil {
				worn[m.Pawn] = map[policy.Resource]bool{}
			}
			if worn[m.Pawn][def] {
				t.Fatalf("order %+v dresses %s twice for one slot", m, m.Pawn)
			}
			worn[m.Pawn][def] = true
		}
		// The next census: each pawn wears what it took, so it is offered
		// neither a taken item nor another of a definition it now wears.
		for i := range obs.Pawns {
			p := &obs.Pawns[i]
			p.Blocked = false
			candidates, _ := p.Candidates.Value()
			var left []policy.GearCandidate
			for _, c := range candidates {
				if !targets[c.Target] && !worn[p.Pawn][c.Definition] {
					left = append(left, c)
				}
			}
			p.Candidates = domain.Known(left)
		}
	}
	for _, p := range obs.Pawns {
		if !worn[p.Pawn][rosterShirt] || !worn[p.Pawn][rosterTuque] {
			t.Fatalf("%s left without shirt and tuque: %v", p.Pawn, worn[p.Pawn])
		}
	}
}

const (
	rosterShirt = "Apparel_BasicShirt"
	rosterTuque = "Apparel_Tuque"
)

func hasDefinition(candidates []policy.GearCandidate, def policy.Resource) bool {
	for _, c := range candidates {
		if c.Definition == def {
			return true
		}
	}
	return false
}

// named gives a recorded pawn's outfit census the short name the recording
// predates.
func named(p policy.GearPawn, name string) policy.GearPawn {
	if state, known := p.Policy.Value(); known {
		state.PawnName = name
		p.Policy = domain.Known(state)
	}
	return p
}
