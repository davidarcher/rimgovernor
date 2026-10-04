package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A stranger corpse goes to the butcher while it is fresh and the butchery
// is open, and to the incinerator otherwise; never anywhere else (#1822).
func TestStrangerCorpseRouting(t *testing.T) {
	for _, open := range []bool{true, false} {
		for _, rot := range []domain.RotStage{domain.RotFresh, domain.RotRotting, domain.RotDessicated, ""} {
			want := StrangerIncinerate
			if open && !rot.Spoiled() {
				want = StrangerButcher
			}
			if got := RouteStranger(rot, open); got != want {
				t.Errorf("open=%v rot=%q: %s", open, rot, got)
			}
		}
	}
}

// Only a rotting animal corpse waits for the rotten dump; a fresh one waits
// for the freezer or the fresh dump, and a human one for the corpse dump.
func TestDumpNeedsRoutesCorpsesByKindAndRot(t *testing.T) {
	facts := RoundsFacts{Waste: domain.Known([]WasteItem{
		{Kind: "corpse", CorpseOf: domain.CorpseAnimal, RotStage: domain.RotFresh},
		{Kind: "corpse", CorpseOf: domain.CorpseAnimal, RotStage: domain.RotRotting},
		{Kind: "corpse", CorpseOf: domain.CorpseStranger, RotStage: domain.RotRotting},
	})}
	needs := DumpNeeds(facts)
	if needs[domain.RottenDumpRole] != 1 || needs[domain.CorpseDumpRole] != 1 {
		t.Fatalf("needs %v", needs)
	}
}
