package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

const artBenchKind = "TableSculpting"

func sculptureSpec(worker string) OrderSpec {
	return OrderSpec{Recipe: SculptureRecipe, Worker: worker, Mode: domain.GearBatch, Target: 1, BenchKind: artBenchKind}
}

func sculptureBill(id, worker string, active bool) GearBill {
	spec := sculptureSpec(worker)
	return GearBill{ID: id, Recipe: SculptureRecipe, Role: domain.RoleSculpture, Active: domain.Known(active), Worker: domain.Known(worker), Spec: domain.Known(spec), Spent: !active}
}

func artGearBench(bills ...GearBill) []GearBench {
	return []GearBench{{ID: "TableSculpting_1", Def: artBenchKind, Usable: domain.Known(true), Bills: domain.Known(bills),
		Recipes: domain.Known([]GearRecipe{{Definition: SculptureRecipe, Role: domain.RoleSculpture, Available: domain.Known(true), AvailableOn: domain.Known(true)}})}}
}

func artRequest(benches []GearBench, owed domain.Fact[bool], profiles ...PawnProfile) ArtOrderRequest {
	return ArtOrderRequest{Benches: benches, Profiles: domain.Known(profiles), Colonists: domain.Known[int64](3), Items: CoreItemFacts(), Demand: ArtDemand{Items: CoreItemFacts()}, RoomsOwed: owed}
}

// An owed room gives each artist lacking a sculpture a pinned batch on the
// bench kind; the artist who has one keeps it, declared as it stands.
func TestDeclareArtOrdersPinsASculptureToEachArtist(t *testing.T) {
	artistA, artistB := artProfile("a", 8, ""), artProfile("b", 8, "")
	got := DeclareArtOrders(artRequest(artGearBench(sculptureBill("Bill_1", "a", true)), domain.Known(true), artistA, artistB))
	if got.Abstain || len(got.Orders) != 2 || got.Orders[0].Key() != sculptureSpec("a").Key() || got.Orders[1].Key() != sculptureSpec("b").Key() {
		t.Fatalf("declared = %+v", got)
	}
	// A finished sculpture is renewed, not declared as it stands.
	got = DeclareArtOrders(artRequest(artGearBench(sculptureBill("Bill_1", "a", false)), domain.Known(true), artistA))
	if len(got.Orders) != 1 || got.Orders[0].Key() != sculptureSpec("a").Key() {
		t.Fatalf("renewed = %+v", got)
	}
}

// A sculpture pinned to someone who is no longer an artist is not declared,
// so the ledger removes it once it has been an orphan for the grace rounds; a
// current artist's is kept.
func TestDeclareArtOrdersDropsASculptureOfAFormerArtist(t *testing.T) {
	benches := artGearBench(sculptureBill("Bill_a", "a", true), sculptureBill("Bill_b", "b", true))
	got := DeclareArtOrders(artRequest(benches, domain.Known(true), artProfile("a", 8, ""), artProfile("b", 2, "")))
	if got.Abstain || len(got.Orders) != 1 || got.Orders[0].Key() != sculptureSpec("a").Key() {
		t.Fatalf("declared = %+v", got)
	}
	actual, known := LedgerActuals(benches)
	if !known {
		t.Fatal("benches unread")
	}
	for i := range actual {
		actual[i].Migrated = LedgerMigratedOwner(MaintainArt)
	}
	orphans := map[string]int{}
	var plan LedgerPlan
	for round := 0; round < OrphanGraceRounds; round++ {
		plan = ReconcileLedger([]Declared{got}, actual, orphans, nil)
		orphans = plan.Orphans
	}
	if len(plan.Remove) != 1 || plan.Remove[0].ID != "Bill_b" || len(plan.Place) != 0 {
		t.Fatalf("plan = %+v", plan)
	}
}

// With the need known gone the ledger removes the sculptures; an unread need
// declares what stands and abstains, so nothing is removed on it.
func TestDeclareArtOrdersFollowsTheNeed(t *testing.T) {
	benches := artGearBench(sculptureBill("Bill_a", "a", true))
	artist := artProfile("a", 8, "")
	if got := DeclareArtOrders(artRequest(benches, domain.Known(false), artist)); got.Abstain || len(got.Orders) != 0 {
		t.Fatalf("no need: %+v", got)
	}
	if got := DeclareArtOrders(artRequest(benches, domain.Unknown[bool](), artist)); !got.Abstain || len(got.Orders) != 1 {
		t.Fatalf("unread need: %+v", got)
	}
	request := artRequest(benches, domain.Unknown[bool](), artist)
	request.Profiles = domain.Unknown[[]PawnProfile]()
	if got := DeclareArtOrders(request); !got.Abstain || len(got.Orders) != 0 {
		t.Fatalf("unread artists: %+v", got)
	}
	// Silver to raise opens the need without a room.
	request = artRequest(artGearBench(), domain.Known(false), artist)
	request.Demand.Sale, request.Demand.Stock, request.Demand.Skill = true, map[Resource]int64{"Gold": 200, "Steel": 1000}, map[PawnID]int{"a": 8}
	if got := DeclareArtOrders(request); len(got.Orders) != 1 || got.Orders[0].Worker != "a" || got.Orders[0].BenchKind != artBenchKind {
		t.Fatalf("sale: %+v", got)
	}
}

// A bill an unmigrated owner placed is kept however long no planner declares
// it; the same bill under a migrated owner is an orphan.
func TestLedgerKeepsAnUnmigratedOwnersBill(t *testing.T) {
	benches := artGearBench(sculptureBill("Bill_x", "x", true))
	actual, _ := LedgerActuals(benches)
	for _, owner := range []ConcernID{MaintainResource, EnsureCooking, MaintainSurgery, MaintainMechs} {
		if LedgerMigratedOwner(owner) {
			t.Fatalf("%s is not migrated", owner)
		}
	}
	for _, owner := range []ConcernID{MaintainArt, MaintainPopulation} {
		if !LedgerMigratedOwner(owner) {
			t.Fatalf("%s is migrated", owner)
		}
	}
	orphans := map[string]int{}
	for round := 0; round < 3*OrphanGraceRounds; round++ {
		actual[0].Migrated = LedgerMigratedOwner(MaintainResource)
		plan := ReconcileLedger([]Declared{{}}, actual, orphans, nil)
		orphans = plan.Orphans
		if len(plan.Remove) != 0 {
			t.Fatalf("round %d removed an unmigrated owner's bill", round)
		}
	}
}

func TestSculptureInProgress(t *testing.T) {
	if !SculptureInProgress(artGearBench(sculptureBill("Bill_1", "a", true))) {
		t.Fatal("an active sculpture bill is sculpting")
	}
	if SculptureInProgress(artGearBench(sculptureBill("Bill_1", "a", false))) {
		t.Fatal("a finished sculpture bill is not")
	}
}
