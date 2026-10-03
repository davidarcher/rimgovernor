package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/snapshot"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	mp "github.com/davidarcher/RimGovernor/go/internal/wire/mirrorpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

// The defense family's threat responses (#744), replayed from defense
// steps recorded with RIMGOVERNOR_SNAPSHOT_DIR on acceptance runs of the
// retired native cases from the retired defense-layout checkpoint (see
// docs/developers/testing/colony-snapshots.md). Each file is the step that
// admitted the case's answer.

// defense/raid-bypass: an ImmediateAttackSappers raid bypasses the line and
// is answered with squad defense, never a line position.
func TestDefenseReplaySapperRaidBypassesTheLine(t *testing.T) {
	t.Parallel()
	results, methods, db := replayDefense(t, "testdata/defense/raid-bypass-sappers.json.gz")
	wantTactic(t, db, methods[0], results[0].Plan, policy.TacticSquad)
	if melee, ranged := squadAttacks(t, db, results[0].Plan); len(melee)+len(ranged) == 0 {
		t.Fatal("squad defense attacks no raider")
	}
}

// defense/siege: a Siege raid still travelling (its supplies not landed)
// is answered by the siege tactic holding at home (#776): attacking now
// makes them flee.
func TestDefenseReplaySiegeHoldsWhileTravelling(t *testing.T) {
	t.Parallel()
	results, methods, db := replayDefense(t, "testdata/defense/siege.json.gz")
	wantTactic(t, db, methods[0], results[0].Plan, policy.TacticSiege)
	fight, _, _ := db.LoadCombatFight(context.Background(), results[0].Plan)
	for _, role := range fight.Memory.Roles {
		if role.Target != "" {
			t.Fatalf("sortie before the camp: %+v", role)
		}
	}
}

// meleeStep is defense/raid-bypass with the colonists' weapons read as
// melee weapons, and the frame's combat rows giving every colonist
// melee power 5 and every hostile foe (#969).
func meleeStep(t *testing.T, foe float64) (snapshot.Defense, []*mp.CombatPawn) {
	t.Helper()
	step, err := snapshot.LoadDefense("testdata/defense/raid-bypass-sappers.json.gz")
	if err != nil {
		t.Fatal(err)
	}
	reply := &o.ListPawnsReply{}
	if err = protojson.Unmarshal(step.CombatPawns, reply); err != nil {
		t.Fatal(err)
	}
	var mirror []*mp.CombatPawn
	for _, p := range reply.GetObserved().GetPawns() {
		row := &mp.CombatPawn{Id: proto.String(p.GetPawn().GetId()), Side: mp.CombatSide_COMBAT_SIDE_COLONIST.Enum(), Cell: p.GetPawn().GetPosition(),
			Downed: proto.Bool(p.GetDowned()), Dead: proto.Bool(p.GetDead()), Health: proto.Float64(1), MeleePower: proto.Float64(5)}
		if p.GetHostile() {
			row.Side, row.MeleePower = mp.CombatSide_COMBAT_SIDE_HOSTILE.Enum(), proto.Float64(foe)
		} else {
			for _, item := range p.GetEquipment().GetEquipped() {
				item.Ranged, item.Range = proto.Bool(false), nil
			}
		}
		mirror = append(mirror, row)
	}
	if step.CombatPawns, err = protojson.Marshal(reply); err != nil {
		t.Fatal(err)
	}
	return step, mirror
}

// defense/raid-bypass fought in melee: a pair whose melee power beats the
// raider's engages it, a pair it outmatches does not and shelters (#969).
func TestDefenseSnapshotMeleeEngagesOnlyABeatableRaider(t *testing.T) {
	t.Parallel()
	step, mirror := meleeStep(t, 8)
	results, methods, db := replayDefenseSteps(t, replayFrame{mirror: mirror}, step)
	wantTactic(t, db, methods[0], results[0].Plan, policy.TacticSquad)
	if melee, ranged := squadAttacks(t, db, results[0].Plan); len(melee) == 0 || len(ranged) != 0 {
		t.Fatal("no melee on a raider the pair beats", melee, ranged)
	}
	step, mirror = meleeStep(t, 12)
	results, methods, db = replayDefenseSteps(t, replayFrame{mirror: mirror}, step)
	wantTactic(t, db, methods[0], results[0].Plan, policy.TacticShelter)
	fight, _, _ := db.LoadCombatFight(context.Background(), results[0].Plan)
	for _, role := range fight.Memory.Roles {
		if role.Target != "" {
			t.Fatal("engaged a raider the pair cannot beat", role)
		}
	}
}

// defense/drop: a centre-drop assault lands beside the colony; the layout
// is irrelevant and squad defense engages at the threat with no defender
// routed to a firing cell.
func TestDefenseReplayCenterDropIsSquadDefense(t *testing.T) {
	t.Parallel()
	results, methods, db := replayDefense(t, "testdata/defense/drop-center.json.gz")
	wantTactic(t, db, methods[0], results[0].Plan, policy.TacticSquad)
	if melee, ranged := squadAttacks(t, db, results[0].Plan); melee["Thing_Human53013"]+ranged["Thing_Human53013"] == 0 {
		t.Fatal("squad defense does not engage the dropped raider")
	}
}

// defense/drop with the raider read as a mechanoid and no layout stored:
// the mech still gets a squad (#970).
func TestDefenseSnapshotMechWithoutLayoutIsSquadDefense(t *testing.T) {
	t.Parallel()
	step, err := snapshot.LoadDefense("testdata/defense/drop-center.json.gz")
	if err != nil {
		t.Fatal(err)
	}
	step.Layout = nil
	reply := &o.ListPawnsReply{}
	if err = protojson.Unmarshal(step.CombatPawns, reply); err != nil {
		t.Fatal(err)
	}
	for _, p := range reply.GetObserved().GetPawns() {
		if p.GetHostile() {
			p.KindDefName, p.Humanlike, p.Mechanoid = proto.String("Mech_Scyther"), proto.Bool(false), proto.Bool(true)
		}
	}
	if step.CombatPawns, err = protojson.Marshal(reply); err != nil {
		t.Fatal(err)
	}
	results, methods, db := replayDefenseSteps(t, replayFrame{}, step)
	wantTactic(t, db, methods[0], results[0].Plan, policy.TacticSquad)
	if melee, ranged := squadAttacks(t, db, results[0].Plan); melee["Thing_Human53013"]+ranged["Thing_Human53013"] == 0 {
		t.Fatal("squad defense does not engage the mech")
	}
}

// defense/predator: a wild cougar hunting a colonist is answered with
// squad defense on the predator.
func TestDefenseReplayHuntingPredatorIsSquadDefense(t *testing.T) {
	t.Parallel()
	results, methods, db := replayDefense(t, "testdata/defense/predator-hunt.json.gz")
	wantTactic(t, db, methods[0], results[0].Plan, policy.TacticSquad)
	if melee, ranged := squadAttacks(t, db, results[0].Plan); melee["Thing_Cougar53013"]+ranged["Thing_Cougar53013"] == 0 {
		t.Fatal("squad defense does not attack the predator")
	}
}

// defense/hive: an insect hive near the colony is an infestation (#1071)
// whose fighters target the hive, melee or ranged from a line of fire.
func TestDefenseReplayHiveIsTargeted(t *testing.T) {
	t.Parallel()
	results, methods, db := replayDefense(t, "testdata/defense/hive.json.gz")
	wantTactic(t, db, methods[0], results[0].Plan, policy.TacticInfestation)
	fight, _, _ := db.LoadCombatFight(context.Background(), results[0].Plan)
	for _, role := range fight.Memory.Roles {
		if role.Target != "Thing_Hive53013" {
			t.Fatalf("a fighter not on the hive: %+v", fight.Memory.Roles)
		}
	}
}

// defense/hive with the hive read passive (dormant, or awake with nobody
// inside its boundary and no insect engaging): the recorded census holds
// nothing and the fight has no target (#948).
func TestDefenseSnapshotPassiveHiveIsLeftAlone(t *testing.T) {
	t.Parallel()
	step, err := snapshot.LoadDefense("testdata/defense/hive.json.gz")
	if err != nil {
		t.Fatal(err)
	}
	facts := step.Emergency
	if len(facts.Threats) == 0 || !policy.ThreatHolds(facts.Threats[0]) {
		t.Fatal("the recorded hive does not hold", facts.Threats)
	}
	for i := range facts.Threats {
		facts.Threats[i].Passive = domain.Known(true)
		if policy.ThreatHolds(facts.Threats[i]) {
			t.Fatal("a passive threat holds", facts.Threats[i].ID)
		}
	}
	if ids, _, buildings := defenseTargets(facts.Threats); len(ids)+len(buildings) != 0 {
		t.Fatal("a passive hive is a defense target", ids, buildings)
	}
}

// defense/shippart: with every colonist carrying a rifle, a crashed ship
// part is shot by a defender with a line of fire.
func TestDefenseReplayShipPartIsShotFromALineOfFire(t *testing.T) {
	t.Parallel()
	results, methods, db := replayDefense(t, "testdata/defense/shippart-rifles.json.gz")
	wantTactic(t, db, methods[0], results[0].Plan, policy.TacticSquad)
	melee, ranged := squadAttacks(t, db, results[0].Plan)
	if ranged["Thing_DefoliatorShipPart53021"] == 0 {
		t.Fatal("no ranged attack on the ship part")
	}
	// Destroyed from range (#930): nobody walks up to the part in melee.
	if melee["Thing_DefoliatorShipPart53021"] != 0 {
		t.Fatal("a melee attack on the ship part")
	}
}

// defense/shippart with a colony mortar 35 cells from the part: the
// mortar is crewed and aimed at the part (#930, #931).
func TestDefenseSnapshotMortarShellsTheShipPart(t *testing.T) {
	t.Parallel()
	step, err := snapshot.LoadDefense("testdata/defense/shippart-rifles.json.gz")
	if err != nil {
		t.Fatal(err)
	}
	var part domain.Cell
	for _, threat := range step.Emergency.Threats {
		if threat.ID == "Thing_DefoliatorShipPart53021" && len(threat.Cells) > 0 {
			part = threat.Cells[0]
		}
	}
	gun := domain.Cell{X: part.X, Z: part.Z + 35}
	if part.Z >= 35 {
		gun.Z = part.Z - 35
	}
	mortar := &mp.CombatMortarRow{Id: proto.String("Thing_Turret_Mortar1"), Cell: &c.Cell{X: proto.Int32(gun.X), Z: proto.Int32(gun.Z)}, MinRange: proto.Float32(29.9), MaxRange: proto.Float32(500)}
	results, _, db := replayDefenseSteps(t, replayFrame{mortars: []*mp.CombatMortarRow{mortar}}, step)
	fight, _, _ := db.LoadCombatFight(context.Background(), results[0].Plan)
	for _, role := range fight.Memory.Roles {
		if role.Mortar != nil && *role.Mortar == gun && role.Aim != nil && *role.Aim == part {
			return
		}
	}
	t.Fatalf("no crew aims the mortar at the part %v: %+v", part, fight.Memory.Roles)
}

// defense/raid-breach: an edge assault is held from the firing line; once
// a raider is behind the line the hold is dropped. The recording has one
// free armed colonist and five unarmed ones against four melee raiders: no
// armed pair per raider, so no squad forms and nobody brawls with fists
// (#948). Instead every free colonist is moved away from the raiders
// (#968; the recording has no roofed room). The later steps replay as one
// fight.
func TestDefenseReplayBreachWithoutArmedPairsFormsNoSquad(t *testing.T) {
	t.Parallel()
	results, _, db := replayDefense(t,
		"testdata/defense/raid-breach-1-hold.json.gz",
		"testdata/defense/raid-breach-2-held.json.gz",
		"testdata/defense/raid-breach-3-fallback.json.gz",
		"testdata/defense/raid-breach-4-squad.json.gz")
	if results[0].Verdict != BuildingReasonAdmitted {
		t.Fatal(results)
	}
	if results[1].Verdict != BuildingReasonExistingWork || results[2].Verdict != BuildingReasonCombatOrders || results[3].Verdict != BuildingReasonExistingWork {
		t.Fatal(results)
	}
	for _, r := range results[1:] {
		if r.Plan != results[0].Plan {
			t.Fatal("the fight changed plans", results)
		}
	}
	fight, _, err := db.LoadCombatFight(context.Background(), results[3].Plan)
	if err != nil || fight.Memory.Tactic != policy.TacticShelter || len(fight.Memory.Roles) < 2 {
		t.Fatalf("fight %+v (%v), want shelter", fight.Memory, err)
	}
	for _, role := range fight.Memory.Roles {
		if role.Target != "" || role.Cell == nil {
			t.Fatal("a shelter role engages or stays", role)
		}
	}
	moves := 0
	for _, o := range fight.Memory.Issued {
		if o.Kind == policy.OrderAttack {
			t.Fatal("an unviable squad attacked", o)
		}
		if o.Kind == policy.OrderMove && o.Reason == policy.ReasonRetreat {
			moves++
		}
	}
	if moves != len(fight.Memory.Roles) {
		t.Fatalf("%d retreat moves for %d roles", moves, len(fight.Memory.Roles))
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
// an unchanged firing line. The recording's lines of fire were probed for
// the pre-#1544 slots, so every slot proposed now is given a known line.
func TestDefenseReplayStockedColonyProposesMoreTurrets(t *testing.T) {
	t.Parallel()
	l := loadLayout(t, "testdata/defense/layout-turrets.json.gz", snapshot.LayoutTurrets)
	wide := l.Request
	wide.Turret.Max = policy.TurretBudget(domain.Known(1e6))
	_, slots, err := policy.DefenseTurrets(wide, l.Geometry)
	if err != nil {
		t.Fatal(err)
	}
	l.Request.Lines = append([]policy.DefenseLine{}, l.Request.Lines...)
	for _, c := range slots {
		l.Request.Lines = append(l.Request.Lines, policy.DefenseLine{From: c.Cell, To: l.Geometry.Approach[0], LineOfSight: domain.Known(true)})
	}
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
