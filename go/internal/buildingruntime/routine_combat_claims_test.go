package buildingruntime

import (
	"context"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/store/storetest"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// A role pawn the fight holds no claim on (a later evacuee, #911) is
// drafted at the stop, unless it is down; an attack refused cannot_hit is
// remembered from the shooter's cell (#912), any other refusal forgotten.
func TestUnclaimedRolesAndCannotHitRecord(t *testing.T) {
	t.Parallel()
	cell := domain.Cell{X: 3, Z: 4}
	view := policy.CombatView{Tick: 9, Pawns: []policy.CombatPawnState{{ID: "a", Cell: domain.Known(cell)}, {ID: "down", Downed: true}}}
	m := policy.CombatMemory{Roles: []policy.CombatRole{{Pawn: "a"}, {Pawn: "evac", Duty: policy.DutyEvacuee}, {Pawn: "down"}}}
	if got := unclaimedRoles(m, map[domain.PawnID]string{"a": "c1"}, view); !slices.Equal(got, []domain.PawnID{"evac"}) {
		t.Fatalf("unclaimed %v", got)
	}
	orders := []policy.CombatOrder{{Pawn: "a", Kind: policy.OrderAttack, Target: "h"}, {Pawn: "evac", Kind: policy.OrderMove, Cell: cell}}
	results := []bridge.CombatOrderResult{{Refusal: bridge.CombatRefusalCannotHit}, {Refusal: bridge.CombatRefusalUnreachable}}
	record, next := combatStopRecord(view, orders, results, m)
	if len(record.Orders) != 2 || !slices.Equal(next.CannotHit, []policy.HitRefusal{{Pawn: "a", Target: "h", From: cell}}) {
		t.Fatalf("record %+v memory %+v", record, next.CannotHit)
	}
}

// An uncertain admission batch leaves the fight's claims unknown (#910):
// the next stop's frame rows settle them. A pawn a claim owns is the
// fight's and orderable, an unowned one was never drafted and leaves the
// fight, and an unread claim stays unknown and unordered.
func TestLearnFightClaimsFromFrameRows(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	journal, err := store.Open(ctx, storetest.Path(t))
	if err != nil {
		t.Fatal(err)
	}
	defer journal.Close()
	plan, err := domain.NewPlan("fight", 1, nil)
	if err != nil {
		t.Fatal(err)
	}
	if err = journal.CreatePlan(ctx, plan); err != nil {
		t.Fatal(err)
	}
	world := store.World{Colony: "colony", Load: "load", Map: 0}
	if err = journal.OpenCombatFight(ctx, "fight", policy.CombatMemory{}, world, []domain.PawnID{"a", "b", "c", "d"}); err != nil {
		t.Fatal(err)
	}
	if err = journal.RecordCombatClaims(ctx, "fight", map[domain.PawnID]string{"d": "claim-d"}, nil); err != nil {
		t.Fatal(err)
	}
	owned := &n.DraftClaimObservation{State: &n.DraftClaimObservation_Owned{Owned: &n.OwnedDraftClaim{ClaimId: proto.String("claim-a")}}}
	unowned := &n.DraftClaimObservation{State: &n.DraftClaimObservation_Unowned{Unowned: &n.NoOwnedDraftClaim{}}}
	rows := map[string]*n.PawnState{"a": {DraftClaim: owned}, "b": {DraftClaim: unowned}, "c": {}, "d": {DraftClaim: unowned}}
	fight, _, err := journal.LoadCombatFight(ctx, "fight")
	if err != nil {
		t.Fatal(err)
	}
	orderable, err := learnFightClaims(ctx, journal, "fight", fight, rows)
	if err != nil || !slices.Equal(orderable, []domain.PawnID{"a", "d"}) {
		t.Fatal(orderable, err)
	}
	fight, _, err = journal.LoadCombatFight(ctx, "fight")
	if err != nil || fight.Claims["a"] != "claim-a" || fight.Claims["d"] != "claim-d" || len(fight.Claims) != 3 {
		t.Fatal(fight.Claims, err)
	}
	if c, held := fight.Claims["c"]; !held || c != "" {
		t.Fatal("an unread claim was settled", fight.Claims)
	}
}
