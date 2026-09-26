package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/snapshot"
)

// The defense family's threat responses (#744), replayed from defense
// steps recorded with RIMGOVERNOR_SNAPSHOT_DIR on acceptance runs of the
// retired native cases from the committed defense-layout checkpoint (see
// docs/developers/testing/colony-snapshots.md). Each file is the step that
// admitted the case's answer.

// defense/raid-bypass: an ImmediateAttackSappers raid bypasses the line and
// is answered with squad defense, never a line position.
func TestDefenseReplaySapperRaidBypassesTheLine(t *testing.T) {
	t.Parallel()
	results, methods, db := replayDefense(t, "testdata/defense/raid-bypass-sappers.json.gz")
	wantMethod(t, methods[0], "squad-")
	if melee, ranged := squadAttacks(t, db, results[0].Plan); len(melee)+len(ranged) == 0 {
		t.Fatal("squad defense attacks no raider")
	}
}

// defense/siege: a Siege raid that never enters the corridor is answered
// with a squad sortie on the besiegers, never a line position.
func TestDefenseReplaySiegeIsASquadSortie(t *testing.T) {
	t.Parallel()
	results, methods, db := replayDefense(t, "testdata/defense/siege.json.gz")
	wantMethod(t, methods[0], "squad-")
	if melee, ranged := squadAttacks(t, db, results[0].Plan); len(melee)+len(ranged) == 0 {
		t.Fatal("the sortie attacks no besieger")
	}
}

// defense/drop: a centre-drop assault lands beside the colony; the layout
// is irrelevant and squad defense engages at the threat with no defender
// routed to a firing cell.
func TestDefenseReplayCenterDropIsSquadDefense(t *testing.T) {
	t.Parallel()
	results, methods, db := replayDefense(t, "testdata/defense/drop-center.json.gz")
	wantMethod(t, methods[0], "squad-")
	if melee, ranged := squadAttacks(t, db, results[0].Plan); melee["Thing_Human53013"]+ranged["Thing_Human53013"] == 0 {
		t.Fatal("squad defense does not engage the dropped raider")
	}
}

// defense/predator: a wild cougar hunting a colonist is answered with
// squad defense on the predator.
func TestDefenseReplayHuntingPredatorIsSquadDefense(t *testing.T) {
	t.Parallel()
	results, methods, db := replayDefense(t, "testdata/defense/predator-hunt.json.gz")
	wantMethod(t, methods[0], "squad-")
	if melee, ranged := squadAttacks(t, db, results[0].Plan); melee["Thing_Cougar53013"]+ranged["Thing_Cougar53013"] == 0 {
		t.Fatal("squad defense does not attack the predator")
	}
}

// defense/hive: an insect hive near the colony is a hostile building the
// squad targets, melee or ranged from a line of fire.
func TestDefenseReplayHiveIsSquadTargeted(t *testing.T) {
	t.Parallel()
	results, methods, db := replayDefense(t, "testdata/defense/hive.json.gz")
	wantMethod(t, methods[0], "squad-")
	if melee, ranged := squadAttacks(t, db, results[0].Plan); melee["Thing_Hive53013"]+ranged["Thing_Hive53013"] == 0 {
		t.Fatal("squad defense does not target the hive")
	}
}

// defense/shippart: with every colonist carrying a rifle, a crashed ship
// part is shot by a defender with a line of fire.
func TestDefenseReplayShipPartIsShotFromALineOfFire(t *testing.T) {
	t.Parallel()
	results, methods, db := replayDefense(t, "testdata/defense/shippart-rifles.json.gz")
	wantMethod(t, methods[0], "squad-")
	if _, ranged := squadAttacks(t, db, results[0].Plan); ranged["Thing_DefoliatorShipPart53021"] == 0 {
		t.Fatal("no ranged attack on the ship part")
	}
}

// defense/raid-breach: an edge assault is held from the firing line; once
// a raider is behind the line the hold is cancelled and the intruders are
// answered with squad defense at the threat.
func TestDefenseReplayBreachFallsBackToSquadDefense(t *testing.T) {
	t.Parallel()
	results, methods, db := replayDefense(t,
		"testdata/defense/raid-breach-1-hold.json.gz",
		"testdata/defense/raid-breach-2-held.json.gz",
		"testdata/defense/raid-breach-3-fallback.json.gz",
		"testdata/defense/raid-breach-4-squad.json.gz")
	wantMethod(t, methods[0], "hold-")
	if results[1].Reason != BuildingMethodExistingWork || results[2].Reason != BuildingMethodHoldFallback {
		t.Fatal(results)
	}
	hold, err := db.LoadPlan(t.Context(), results[0].Plan)
	if err != nil {
		t.Fatal(err)
	}
	for _, p := range hold.Progress {
		if v := p.View(); v.Stage != domain.Cancelled && v.Stage != domain.Completed {
			t.Fatalf("%s is %s after the fallback", v.Action, v.Stage)
		}
	}
	wantMethod(t, methods[3], "squad-")
	if melee, ranged := squadAttacks(t, db, results[3].Plan); len(melee)+len(ranged) == 0 {
		t.Fatal("squad defense attacks no intruder")
	}
}

func loadLayout(t *testing.T, path, point string) snapshot.Layout {
	t.Helper()
	l, err := snapshot.LoadLayout(path)
	if err != nil {
		t.Fatal(err)
	}
	if l.Point != point {
		t.Fatalf("%s records %s, want %s", path, l.Point, point)
	}
	return l
}

// defense/layout: on the fixture-constrained tribal8 site the planner
// chooses the corridor chokepoint and proposes every tier with a verified
// firing line (recorded from the proposal's second, lines-of-fire pass).
func TestDefenseReplayLayoutChoosesTheCorridor(t *testing.T) {
	t.Parallel()
	l := loadLayout(t, "testdata/defense/layout-propose.json.gz", snapshot.LayoutPropose)
	layout, err := policy.DefenseLayouts(l.Request)
	if err != nil || !layout.LinesVerified {
		t.Fatal(layout.LinesVerified, err)
	}
	if len(layout.TrapLane) == 0 || len(layout.SafeLane) == 0 || len(layout.Firing) == 0 {
		t.Fatalf("lanes %d/%d firing %d", len(layout.TrapLane), len(layout.SafeLane), len(layout.Firing))
	}
	tiers := map[policy.DefenseTierName]bool{}
	for _, tier := range layout.Tiers {
		tiers[tier.Name] = len(tier.Buildings) > 0
	}
	if !tiers[policy.TierFiringLine] {
		t.Fatalf("tiers %v", tiers)
	}
}

// defense/turrets: with turret research, a fuelled network and steel
// observed, turrets are placed behind the firing line with a conduit chain.
func TestDefenseReplayTurretsJoinTheLayout(t *testing.T) {
	t.Parallel()
	l := loadLayout(t, "testdata/defense/layout-turrets.json.gz", snapshot.LayoutTurrets)
	tier, _, err := policy.DefenseTurrets(l.Request, l.Geometry)
	if err != nil {
		t.Fatal(err)
	}
	turrets, conduits := 0, 0
	for _, b := range tier.Buildings {
		switch b.Definition() {
		case l.Request.Turret.Definition:
			turrets++
		case l.Request.Turret.Conduit:
			conduits++
		}
	}
	if turrets == 0 || conduits == 0 {
		t.Fatalf("%d turrets, %d conduits", turrets, conduits)
	}
}

// defense/layout-stocked: a stocked colony's higher raid-point band raises
// the turret budget; the same site then proposes more turret positions on
// an unchanged firing line.
func TestDefenseReplayStockedColonyProposesMoreTurrets(t *testing.T) {
	t.Parallel()
	l := loadLayout(t, "testdata/defense/layout-turrets.json.gz", snapshot.LayoutTurrets)
	count := func(r policy.DefenseRequest) int {
		tier, _, err := policy.DefenseTurrets(r, l.Geometry)
		if err != nil {
			t.Fatal(err)
		}
		n := 0
		for _, b := range tier.Buildings {
			if b.Definition() == r.Turret.Definition {
				n++
			}
		}
		return n
	}
	base := count(l.Request)
	stocked := l.Request
	if stock, ok := l.Request.Turret.Stock.Value(); ok {
		tripled := map[policy.Resource]int64{}
		for k, v := range stock {
			tripled[k] = 3 * v
		}
		stocked.Turret.Stock = domain.Known(tripled)
	}
	stocked.Turret.Max = policy.TurretBudget(domain.Known(1e6))
	if base >= stocked.Turret.Max {
		t.Fatalf("base %d already at the stocked budget %d", base, stocked.Turret.Max)
	}
	if more := count(stocked); more <= base {
		t.Fatalf("stocked colony proposes %d turrets, base %d", more, base)
	}
}

// defense/cover: the census ranks the approach sectors and cover inside
// the firing line's engagement zone is ordered cleared through the game's
// own designations. Recorded on the defense/layout run's post-raid cover
// clearance: the defense/cover run never reached the census (its layout
// goal stayed bound with no clearance method).
func TestDefenseReplayRaidCrossingOrdersCoverCleared(t *testing.T) {
	t.Parallel()
	l := loadLayout(t, "testdata/defense/layout-cover-after-raid.json.gz", snapshot.LayoutCover)
	layout, err := defenseRecordLayout(*l.Record)
	if err != nil {
		t.Fatal(err)
	}
	approaches, err := policy.DefenseApproachesFor(l.Request, layout)
	if err != nil || approaches.Hold != "" {
		t.Fatal(approaches.Hold, err)
	}
	if len(approaches.Sectors) == 0 || len(approaches.Cover) == 0 {
		t.Fatalf("%d sectors, %d cover", len(approaches.Sectors), len(approaches.Cover))
	}
	byCell := map[domain.Cell]bridge.DefenseCell{}
	for _, cell := range l.Site {
		byCell[cell.Cell] = cell
	}
	clearances, _, err := defenseCoverSelection(approaches, byCell)
	if err != nil || len(clearances) == 0 {
		t.Fatal(len(clearances), err)
	}
}
