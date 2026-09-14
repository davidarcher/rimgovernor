package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func wastePawn(id PawnID, dead, downed, drafted, mental bool) WastePawn {
	return WastePawn{ID: id, Dead: domain.Known(dead), Downed: domain.Known(downed), Drafted: domain.Known(drafted), MentalState: domain.Known(mental)}
}

func TestWasteDeficitUnknownWithoutCensus(t *testing.T) {
	if _, known := WasteDeficit(domain.Unknown[[]WasteItem]()).Value(); known {
		t.Fatal("deficit known without a census")
	}
}

func TestWasteDeficitTrueWhenExposedEligibleItemRemains(t *testing.T) {
	items := []WasteItem{{ID: "a", State: WasteExposed, Eligible: true}}
	deficit, known := WasteDeficit(domain.Known(items)).Value()
	if !known || !deficit {
		t.Fatalf("deficit = %v known = %v", deficit, known)
	}
}

func TestWasteDeficitFalseWhenNothingPending(t *testing.T) {
	items := []WasteItem{
		{ID: "a", State: WasteRelocated, Eligible: true},
		{ID: "b", State: WasteExposed, Eligible: false},
		{ID: "c", State: WasteBuried, Eligible: true},
	}
	deficit, known := WasteDeficit(domain.Known(items)).Value()
	if !known || deficit {
		t.Fatalf("deficit = %v known = %v", deficit, known)
	}
}

func TestSelectWasteMethodPrefersCorpseThenLowestID(t *testing.T) {
	items := []WasteItem{
		{ID: "junk-b", Kind: "junk", State: WasteExposed, Eligible: true},
		{ID: "corpse-z", Kind: "corpse", State: WasteExposed, Eligible: true},
		{ID: "corpse-a", Kind: "corpse", State: WasteExposed, Eligible: true},
	}
	pawns := []WastePawn{wastePawn("colonist1", false, false, false, false)}
	item, pawn, ok := SelectWasteMethod(items, pawns)
	if !ok || item.ID != "corpse-a" || pawn != "colonist1" {
		t.Fatalf("item=%+v pawn=%v ok=%v", item, pawn, ok)
	}
}

func TestSelectWasteMethodIgnoresIneligibleOrNonExposedItems(t *testing.T) {
	items := []WasteItem{
		{ID: "a", State: WasteExposed, Eligible: false},
		{ID: "b", State: WasteRelocated, Eligible: true},
		{ID: "c", State: WasteBuried, Eligible: true},
	}
	pawns := []WastePawn{wastePawn("colonist1", false, false, false, false)}
	if _, _, ok := SelectWasteMethod(items, pawns); ok {
		t.Fatal("selected from ineligible/non-exposed items")
	}
}

func TestSelectWasteMethodPicksLowestIDEligiblePawn(t *testing.T) {
	items := []WasteItem{{ID: "junk1", State: WasteExposed, Eligible: true}}
	pawns := []WastePawn{
		wastePawn("colonist2", false, false, false, false),
		wastePawn("colonist1", false, false, false, false),
		// Excluded for cause.
		wastePawn("colonist0", true, false, false, false),
	}
	_, pawn, ok := SelectWasteMethod(items, pawns)
	if !ok || pawn != "colonist1" {
		t.Fatalf("pawn = %v ok = %v", pawn, ok)
	}
}

func TestSelectWasteMethodExcludesDeadDownedDraftedMentalPawns(t *testing.T) {
	items := []WasteItem{{ID: "junk1", State: WasteExposed, Eligible: true}}
	pawns := []WastePawn{
		wastePawn("dead", true, false, false, false),
		wastePawn("downed", false, true, false, false),
		wastePawn("drafted", false, false, true, false),
		wastePawn("mental", false, false, false, true),
	}
	if _, _, ok := SelectWasteMethod(items, pawns); ok {
		t.Fatal("selected an excluded pawn")
	}
}

func TestSelectWasteMethodUnknownPawnFactsExcluded(t *testing.T) {
	items := []WasteItem{{ID: "junk1", State: WasteExposed, Eligible: true}}
	pawns := []WastePawn{{ID: "colonist1", Dead: domain.Unknown[bool](), Downed: domain.Known(false), Drafted: domain.Known(false), MentalState: domain.Known(false)}}
	if _, _, ok := SelectWasteMethod(items, pawns); ok {
		t.Fatal("selected a pawn with unknown facts")
	}
}

func TestSelectWasteMethodNoEligiblePawns(t *testing.T) {
	items := []WasteItem{{ID: "junk1", State: WasteExposed, Eligible: true}}
	if _, _, ok := SelectWasteMethod(items, nil); ok {
		t.Fatal("selected with no pawns")
	}
}
