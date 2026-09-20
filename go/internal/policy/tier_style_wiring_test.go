package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The tier style decides the floor when its floor can be laid now; a styled
// floor the colony cannot afford for the whole batch, or that fails the
// tier, leaves the scored list to decide (#610).
func TestSelectFlooringPrefersTheTierStyle(t *testing.T) {
	p := flooringPolicy()
	p.Floors = append(p.Floors, "TileGranite")
	review, _ := ReviewFlooring(domain.Known(flooringCensus()), domain.Unknown[RoomObservation](), nil, p)
	for _, d := range review.Deficits {
		if d.Room == "kitchen" && d.Role != RoomRoleKitchen || d.Room == "bed" && d.Role != RoomRoleBedroom {
			t.Fatalf("deficit role %+v", d)
		}
	}
	facts := flooringDefinitions()
	facts.Definitions["TileGranite"] = FloorDefinition{Available: domain.Known(true), Terrain: domain.Known(true), Cleanliness: domain.Known(0.0), Beauty: domain.Known(0.0), Flammability: domain.Known(0.0), PathCost: domain.Known[int32](0), Costs: domain.Known([]Amount{{Resource: "BlocksGranite", Count: 3}})}
	stock := TierStyleStock{"BlocksGranite": 100, "WoodLog": 100}
	facts.Stock = domain.Known(map[Resource]int64(stock))
	facts.Style = func(role RoomRole) (string, bool) { return FloorDef(BuildTierMasonry, role, stock, FloorStyleFacts{}) }
	proposal, err := SelectFlooringMethod(review, facts, p)
	if err != nil || proposal.Method != FlooringBuild || proposal.Definition != "TileGranite" || proposal.Room != "kitchen" || len(proposal.Cells) != 6 {
		t.Fatal(proposal, err)
	}
	// Blocks for half the batch: the style stands aside and wood pays.
	facts.Stock = domain.Known(map[Resource]int64{"BlocksGranite": 9, "WoodLog": 100})
	if proposal, err = SelectFlooringMethod(review, facts, p); err != nil || proposal.Definition != "WoodPlankFloor" {
		t.Fatal(proposal, err)
	}
	// A style naming a floor the census does not know is no preference:
	// the scored list decides, and with that stock it scores the tile too.
	facts.Stock = domain.Known(map[Resource]int64(stock))
	facts.Style = func(RoomRole) (string, bool) { return "TileMarble", true }
	if proposal, err = SelectFlooringMethod(review, facts, p); err != nil || proposal.Definition != "TileGranite" {
		t.Fatal(proposal, err)
	}
	// The style outranks the score: carpet in the bedroom over the tile.
	facts.Definitions[Carpet] = FloorDefinition{Available: domain.Known(true), Terrain: domain.Known(true), Cleanliness: domain.Known(0.0), Beauty: domain.Known(2.0), Flammability: domain.Known(1.0), PathCost: domain.Known[int32](0), Costs: domain.Known([]Amount{{Resource: "Cloth", Count: 7}})}
	facts.Stock = domain.Known(map[Resource]int64{"BlocksGranite": 100, "WoodLog": 100, "Cloth": 100})
	facts.Style = func(role RoomRole) (string, bool) {
		return FloorDef(BuildTierMasonry, role, TierStyleStock{"BlocksGranite": 100, "Cloth": 100}, FloorStyleFacts{CarpetMaking: true})
	}
	p.Floors = append(p.Floors, Carpet)
	review.Deficits = review.Deficits[1:] // the bedroom next
	if proposal, err = SelectFlooringMethod(review, facts, p); err != nil || proposal.Room != "bed" || proposal.Definition != Carpet {
		t.Fatal(proposal, err)
	}
}

// The styled lamp is chosen ahead of the policy's ladder when it is known
// available; an unknown or unavailable styled lamp leaves the ladder.
func TestSelectLightingPrefersTheStyledLamp(t *testing.T) {
	site := lightingSite()
	site.Styled = "StandingLamp"
	if lamp, reason := selectLampDefinition(site, DefaultLightingPolicy()); lamp != "StandingLamp" || reason != "" {
		t.Fatal(lamp, reason)
	}
	site.Styled = "SunLamp"
	if lamp, reason := selectLampDefinition(site, DefaultLightingPolicy()); lamp != "TorchLamp" || reason != "" {
		t.Fatal(lamp, reason)
	}
	site.Available["SunLamp"] = domain.Known(false)
	if lamp, reason := selectLampDefinition(site, DefaultLightingPolicy()); lamp != "TorchLamp" || reason != "" {
		t.Fatal(lamp, reason)
	}
}
