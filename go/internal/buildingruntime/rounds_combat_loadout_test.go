package buildingruntime

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	mp "github.com/davidarcher/RimGovernor/go/internal/wire/mirrorpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// podLoadoutNative frames a raid as a drop-pod arrival and serves the
// map's loose weapons.
type podLoadoutNative struct {
	framed
	weapons *equipTestNative
}

func (n podLoadoutNative) ReadCombat(ctx context.Context, identity *c.Identity) (bridge.Combat, error) {
	combat, err := n.framed.ReadCombat(ctx, identity)
	combat.Events = append(combat.Events, &mp.CombatEventRow{Kind: mp.CombatLogKind_COMBAT_LOG_KIND_HOSTILE_ARRIVED.Enum(), RaidStrategy: proto.String(bridge.PodsStrategy),
		At: &mp.Watermark{Tick: proto.Int64(1)}, OpenTick: proto.Int32(1), LandingCells: []*c.Cell{{X: proto.Int32(9), Z: proto.Int32(5)}}})
	return combat, err
}
func (n podLoadoutNative) ReadMapBounds(ctx context.Context, id *c.Identity, at domain.Cell) (bridge.MapBounds, bridge.Result, error) {
	return n.weapons.ReadMapBounds(ctx, id, at)
}
func (n podLoadoutNative) ReadEquipWeapons(ctx context.Context, id *c.Identity, from, to domain.Cell) (bridge.EquipRead, bridge.Result, error) {
	return n.weapons.ReadEquipWeapons(ctx, id, from, to)
}

// A pod fight (#1115) commits its threat loadout as the fight plan's own
// equip actions before the first combat.orders batch; the batch leaves the
// loadout pawn undrafted, and it drafts once its equip settles.
func TestPodFightCommitsLoadoutBeforeFirstCombatBatch(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	r, db, session, _, n := roundsFixture(t)
	ctx := context.Background()
	equip := &equipTestNative{roundsNative: n, ids: []string{"a", "b"},
		weapons: []bridge.EquipCandidate{{Thing: "shotgun", Definition: "Gun_PumpShotgun", Cell: domain.Cell{X: 4, Z: 4}}},
		editPawn: func(row *o.PawnState) {
			row.Biography = &o.PawnBiography{DisabledWorkTags: []string{}}
		}}
	raid := &raidTestNative{equipTestNative: equip, raider: domain.Cell{X: 9, Z: 5}, toil: "LordToil_AssaultColony", weapon: "Gun_Revolver"}
	native := podLoadoutNative{framed: framed{raid}, weapons: equip}
	planner, err := NewRoundsDefensePlanner(r, native)
	if err != nil {
		t.Fatal(err)
	}
	snapshot := session.State().Snapshot
	current, err := db.LoadRounds(ctx)
	if err != nil {
		t.Fatal(err)
	}
	facts := policy.RoundsFacts{Workers: domain.Known(2), Wood: domain.Known(int64(100)), Hostiles: domain.Known(int64(1)), CriticalPatients: domain.Known(int64(0)), CleanupPawns: domain.Known(false), ColonyNaming: domain.Known(false), ChoiceDialog: domain.Known(false)}
	if _, err = db.ReviewRounds(ctx, store.RoundsRequest{Revision: current.Revision, Current: snapshot, Tick: 7, Enabled: true, Policy: policy.DefaultRoundsPolicy(), Facts: facts}); err != nil {
		t.Fatal(err)
	}
	got, err := planner.Step(ctx)
	if err != nil || got.Verdict != BuildingReasonAdmitted {
		t.Fatal(got, err)
	}
	plan, err := db.LoadPlan(ctx, got.Plan)
	if err != nil || len(plan.Spec.Actions()) != 1 {
		t.Fatalf("the fight plan carries no loadout: %v %v", plan.Spec.Actions(), err)
	}
	action, ok := plan.Spec.Actions()[0].Equip()
	if !ok || action.Thing() != "shotgun" {
		t.Fatal(plan.Spec.Actions()[0])
	}
	batches := raid.orders.batches
	if len(batches) != 1 {
		t.Fatal(batches)
	}
	for _, order := range batches[0].Orders {
		if order.GetPawn().GetEntityId() == string(action.Pawn()) {
			t.Fatalf("the loadout pawn %s was ordered before its equip settled: %v", action.Pawn(), order)
		}
	}
	fight, ok, err := db.LoadCombatFight(ctx, got.Plan)
	if err != nil || !ok {
		t.Fatal(err)
	}
	if fight.Roster[action.Pawn()] {
		t.Fatal("the loadout pawn is on the fight roster", fight.Roster)
	}
	// Its equip still open, the next stop leaves it undrafted.
	if _, err = planner.Step(ctx); err != nil {
		t.Fatal(err)
	}
	for _, batch := range raid.orders.batches[1:] {
		for _, order := range batch.Orders {
			if order.GetPawn().GetEntityId() == string(action.Pawn()) {
				t.Fatal("the loadout pawn drafted with its equip open", order)
			}
		}
	}
}
