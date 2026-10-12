package policy

import (
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func blockBuilding(t *testing.T, def string, x, z int32) domain.Building {
	t.Helper()
	b, err := domain.NewBuilding(def, domain.Cell{X: x, Z: z}, domain.North, "")
	if err != nil {
		t.Fatal(err)
	}
	return b
}

func blockPricing() BlockPricing {
	return BlockPricing{
		Work: WorkPrice{WorkDayW: 100, CellWalkDays: 0.001}, TicksPerWorkDay: 20000, SteelValue: 2,
		Market:    map[Resource]float64{"Steel": 2, "WoodLog": 1},
		UnitCosts: map[string][]Amount{"Turret": {{Resource: "Steel", Count: 30}}, "Wall": {{Resource: "Steel", Count: 5}}},
		Rows: map[string]ClearRow{
			ClearKey("Tree", ""):      {Work: 200, Yield: []Amount{{Resource: "WoodLog", Count: 20}}},
			ClearKey("Wall", ""):      {Work: 400},
			ClearKey("Turret", ""):    {Work: 1000},
			ClearKey("BigWall", ""):   {Work: 3000},
			ClearKey("Boulder", ""):   {Work: 0},
			ClearKey("Mountain", ""):  {Work: 0},
			ClearKey("CheapWall", ""): {Work: 20},
		},
	}
}

// blockFixture is a turret tier of two buildings, one standing and one whose
// cell the thing blocks, and the place the census would site it instead.
func blockFixture(t *testing.T, thing CoverThing, movedOnto domain.Cell) ([]DefenseCell, DefenseTierPins, []DefenseTier) {
	t.Helper()
	standing, blocked := blockBuilding(t, "Turret", 10, 10), blockBuilding(t, "Turret", 12, 10)
	census := []DefenseCell{
		{Cell: standing.Cell(), Edifice: "Turret"},
		{Cell: blocked.Cell(), Cover: thing},
		{Cell: movedOnto},
	}
	pins := DefenseTierPins{Tiers: map[DefenseTierName]DefenseTier{TierTurrets: {Name: TierTurrets, Buildings: []domain.Building{standing, blocked}}}}
	sited := []DefenseTier{{Name: TierTurrets, Buildings: []domain.Building{blockBuilding(t, "Turret", movedOnto.X, movedOnto.Z)}}}
	return census, pins, sited
}

// A felled tree gives wood and costs a few ticks of work: clearing it beats
// rebuilding the tier elsewhere.
func TestPriceTierBlocksClearsATreeCheaperThanMoving(t *testing.T) {
	census, pins, sited := blockFixture(t, CoverThing{Def: "Tree", Kind: CoverPlant}, domain.Cell{X: 20, Z: 20})
	got := PriceTierBlocks(census, pins, sited, blockPricing())
	if len(got) != 1 || got[0].Tier != TierTurrets || len(got[0].Blocked) != 1 {
		t.Fatalf("got %+v", got)
	}
	d := got[0]
	if d.Choice != TierBlockClear || d.Reason != ReasonBlockClear || !d.ClearKnown || !d.MoveKnown || d.ClearW >= d.MoveW {
		t.Fatalf("%+v", d)
	}
	if d.ClearW >= 0 {
		t.Fatalf("clear %v is not net of the wood's yield", d.ClearW)
	}
}

// A building that takes long to deconstruct costs more than a move to a cheap
// site, and the move says its downtime is unpriced.
func TestPriceTierBlocksMovesWhenClearingCostsMore(t *testing.T) {
	p := blockPricing()
	p.UnitCosts["Turret"] = []Amount{{Resource: "WoodLog", Count: 1}}
	census, pins, sited := blockFixture(t, CoverThing{Def: "BigWall", Kind: CoverBuilding}, domain.Cell{X: 20, Z: 20})
	d := PriceTierBlocks(census, pins, sited, p)[0]
	if d.Choice != TierBlockMove || !strings.Contains(d.Reason, "downtime_unpriced") || d.MoveW >= d.ClearW {
		t.Fatalf("%+v", d)
	}
}

// Moving onto the blocked cell is no move, and with no site at all the tier
// clears: never a veto.
func TestPriceTierBlocksNeverVetoes(t *testing.T) {
	census, pins, _ := blockFixture(t, CoverThing{Def: "BigWall", Kind: CoverBuilding}, domain.Cell{X: 20, Z: 20})
	for name, sited := range map[string][]DefenseTier{
		"no site":     nil,
		"same blocks": {{Name: TierTurrets, Buildings: []domain.Building{blockBuilding(t, "Turret", 12, 10)}}},
	} {
		d := PriceTierBlocks(census, pins, sited, blockPricing())[0]
		if d.Choice != TierBlockClear || d.MoveKnown {
			t.Fatalf("%s: %+v", name, d)
		}
	}
}

// A hauled chunk costs the walk of a cell; rock is unpriced, so the move is
// the priced way, and with neither priced the tier is reported, not vetoed.
func TestPriceTierBlocksChunkRockAndUnpriced(t *testing.T) {
	census, pins, sited := blockFixture(t, CoverThing{Def: "Boulder", Kind: CoverChunk}, domain.Cell{X: 20, Z: 20})
	d := PriceTierBlocks(census, pins, sited, blockPricing())[0]
	if d.Choice != TierBlockClear || d.ClearW != 0.001*100 {
		t.Fatalf("chunk %+v", d)
	}
	census, pins, sited = blockFixture(t, CoverThing{Def: "Mountain", Kind: CoverMineable}, domain.Cell{X: 20, Z: 20})
	d = PriceTierBlocks(census, pins, sited, blockPricing())[0]
	if d.Choice != TierBlockMove || d.ClearKnown {
		t.Fatalf("rock %+v", d)
	}
	d = PriceTierBlocks(census, pins, nil, blockPricing())[0]
	if d.Choice != TierBlockUnpriced || d.Reason != ReasonBlockUnpriced {
		t.Fatalf("neither %+v", d)
	}
	if d = PriceTierBlocks(census, pins, sited, BlockPricing{})[0]; d.Choice != TierBlockUnpriced {
		t.Fatalf("no pricing %+v", d)
	}
}

// The removal work of the old tier is part of a move, and a tier nothing
// blocks is not priced.
func TestPriceTierBlocksMoveCountsRemovalAndSkipsFreeTiers(t *testing.T) {
	census, pins, sited := blockFixture(t, CoverThing{Def: "Tree", Kind: CoverPlant}, domain.Cell{X: 20, Z: 20})
	p := blockPricing()
	with := PriceTierBlocks(census, pins, sited, p)[0].MoveW
	p.Rows[ClearKey("Turret", "")] = ClearRow{Work: 0}
	if without := PriceTierBlocks(census, pins, sited, p)[0].MoveW; with-without != 1000.0/20000*100 {
		t.Fatalf("removal priced %v", with-without)
	}
	census[1].Cover = CoverThing{}
	if got := PriceTierBlocks(census, pins, sited, p); len(got) != 0 {
		t.Fatalf("unblocked tier priced: %+v", got)
	}
	census, pins, _ = blockFixture(t, CoverThing{Def: "Tree", Kind: CoverPlant}, domain.Cell{X: 20, Z: 20})
	things := BlockedThings(census, pins)
	if len(things) != 2 || things[0].Def != "Tree" || things[1].Def != "Turret" {
		t.Fatalf("things %+v", things)
	}
}
