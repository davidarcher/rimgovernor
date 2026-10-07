package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// filledTomb is a planned tomb whose first sarcophagus holds the corpse(s).
func filledTomb(t *testing.T, held ...WasteItem) (LayoutPlan, PlannedRoom, []CurrentBuilding, []WasteItem) {
	t.Helper()
	plan, room := tombFixture()
	dead := []WasteItem{{ID: "x", Kind: "corpse", State: WasteExposed, CorpseOf: domain.CorpseColonist}}
	first := wantedInterior(NextTombStep(plan, dead, nil, testShapes, true, StrangerTomb{}).Template[0])
	built := []CurrentBuilding{sarcophagus(t, "Sarcophagus_1", first)}
	for i := range held {
		held[i].State, held[i].Grave = WasteBuried, "Sarcophagus_1"
	}
	return plan, room, built, held
}

func TestFilledStrangerSarcophagusIsDeconstructedOnceTheMemoryIsLive(t *testing.T) {
	plan, _, built, waste := filledTomb(t, WasteItem{ID: "Corpse_s1", Kind: "corpse", CorpseOf: domain.CorpseStranger})
	step := NextTombStep(plan, waste, built, testShapes, true, StrangerTomb{Live: 1, Funded: true})
	if step.Kind != TombDispose || len(step.Disposal) != 1 || step.Disposal[0].ID != "Sarcophagus_1" {
		t.Fatalf("filled stranger sarcophagus: %+v", step)
	}
	// Still disposed at the cap, unfunded, and with no sarcophagus research.
	for name, s := range map[string]StrangerTomb{"capped": {Live: StrangerTombStackCap}, "unfunded": {Live: 2}} {
		if step := NextTombStep(plan, waste, built, testShapes, true, s); step.Kind != TombDispose {
			t.Fatalf("%s: %+v", name, step)
		}
	}
	if step := NextTombStep(plan, waste, built, testShapes, false, StrangerTomb{Live: 1}); step.Kind != TombDispose {
		t.Fatalf("grave path: %+v", step)
	}
}

func TestNoDisposalBeforeTheMemoryIsLive(t *testing.T) {
	plan, _, built, waste := filledTomb(t, WasteItem{ID: "Corpse_s1", Kind: "corpse", CorpseOf: domain.CorpseStranger})
	if step := NextTombStep(plan, waste, built, testShapes, true, StrangerTomb{}); step.Kind != TombNone {
		t.Fatalf("unread or not yet live: %+v", step)
	}
}

func TestNoDisposalOfAColonistsSarcophagusOrAGrave(t *testing.T) {
	plan, _, built, waste := filledTomb(t, WasteItem{ID: "Corpse_c1", Kind: "corpse", CorpseOf: domain.CorpseColonist})
	live := StrangerTomb{Live: 2, Funded: true}
	if step := NextTombStep(plan, waste, built, testShapes, true, live); step.Kind == TombDispose {
		t.Fatalf("colonist sarcophagus: %+v", step)
	}
	// A colonist sharing the record with a stranger still protects it.
	mixed := append(waste, WasteItem{ID: "Corpse_s1", Kind: "corpse", State: WasteBuried, CorpseOf: domain.CorpseStranger, Grave: "Sarcophagus_1"})
	if got := StrangerDisposals(plan, mixed, built, testSarcophagus, live); len(got) != 0 {
		t.Fatalf("mixed: %+v", got)
	}
	// A sarcophagus outside the planned tomb is the player's.
	_, _, built, waste = filledTomb(t, WasteItem{ID: "Corpse_s1", Kind: "corpse", CorpseOf: domain.CorpseStranger})
	other := LayoutPlan{Rooms: []PlannedRoom{{Role: PlannedTomb, Interior: Rectangle{X: 40, Z: 40, Width: 5, Height: 5}, Door: domain.Cell{X: 42, Z: 39}, DoorRot: domain.North}}}
	if got := StrangerDisposals(other, waste, built, testSarcophagus, live); len(got) != 0 {
		t.Fatalf("outside the plan: %+v", got)
	}
	// A grave never qualifies.
	g := built[0]
	b, err := domain.NewBuilding(GraveDefinition, g.Building.Cell(), domain.North, "")
	if err != nil {
		t.Fatal(err)
	}
	graves := []CurrentBuilding{{ID: "Sarcophagus_1", Building: b, Cells: g.Cells}}
	if got := StrangerDisposals(plan, waste, graves, testSarcophagus, live); len(got) != 0 {
		t.Fatalf("grave: %+v", got)
	}
}

func TestDisposalFreesTheTombSlot(t *testing.T) {
	plan, room, built, waste := filledTomb(t, WasteItem{ID: "Corpse_s1", Kind: "corpse", CorpseOf: domain.CorpseStranger})
	live := StrangerTomb{Live: 1, Funded: true}
	taken := func(b []CurrentBuilding) map[domain.Cell]bool {
		_, cells := tombCensus(waste, b, testSarcophagus, 0)
		return cells
	}
	first, ok := tombSlot(room, testShapes, taken(nil))
	if !ok {
		t.Fatal("no first slot")
	}
	if held, _ := tombSlot(room, testShapes, taken(built)); held.Slot == first.Slot {
		t.Fatal("the filled sarcophagus did not hold its slot")
	}
	// Once deconstructed (gone from the census) the slot is free again, and the
	// ejected corpse is plain exposed waste, no longer buried.
	if freed, _ := tombSlot(room, testShapes, taken(nil)); freed.Slot != first.Slot {
		t.Fatalf("slot not freed: %v", freed.Slot)
	}
	ejected := []WasteItem{{ID: "Corpse_s1", Kind: "corpse", State: WasteExposed, CorpseOf: domain.CorpseStranger}}
	if step := NextTombStep(plan, ejected, nil, testShapes, true, live); step.Kind == TombDispose || len(step.Disposal) != 0 {
		t.Fatalf("nothing left to dispose: %+v", step)
	}
}

// The ejected corpse reaches the incinerator through the existing filter: the
// zone refuses only what native calls not burnable (BurnableRule), so a stranger
// corpse past Fresh is taken.
func TestIncineratorFilterTakesTheEjectedCorpse(t *testing.T) {
	f := domain.IncineratorFilter()
	if len(f.Allow()) != 0 || len(f.Disallow()) != 1 || f.Disallow()[0] != domain.SpecialFilter(domain.NotBurnableFilterDef) {
		t.Fatalf("incinerator filter: %+v", f)
	}
}
