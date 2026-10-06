package observation

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestBlightCensusReadsPlantsFromTheMirror(t *testing.T) {
	t.Parallel()
	plant := func(id uint64, blighted bool, flags policy.ThingFlags) []policy.Thing {
		return []policy.Thing{{Def: "Plant_Rice", Category: policy.ThingPlant, ID: id, Count: 1, Flags: flags, Plant: policy.PlantState{Growth: 0.5, Blighted: blighted}}}
	}
	cell := func(x int32, zone string, home bool, things []policy.Thing) policy.SiteCell {
		c := policy.SiteCell{Cell: domain.Cell{X: x, Z: 4}, InHome: domain.Known(home), Things: things}
		if zone != "" {
			c.ZoneID = domain.Known(zone)
		}
		return c
	}
	p := ColonyProjection{
		Region: policy.Rectangle{Width: 20, Height: 20},
		Zones:  facts.Held[bridge.ZonesRead]{Complete: true},
		Farms:  []FarmZoneFact{{ID: "7"}},
		Cells: []policy.SiteCell{
			cell(1, "7", false, plant(11, true, policy.FlagDesignated)), // growing zone, designated
			cell(2, "", true, plant(12, true, 0)),                       // home area
			cell(3, "7", false, plant(13, false, 0)),                    // healthy
			cell(4, "", false, plant(14, true, 0)),                      // wild: the storyteller's
			cell(5, "9", false, plant(15, true, 0)),                     // a stockpile zone, not growing, off home
		},
	}
	plants, known := blightCensus(p).Value()
	if !known || len(plants) != 2 {
		t.Fatal(plants, known)
	}
	if got := plants[0]; got.ID != "Thing_Plant_Rice11" || !got.Designated || !got.Eligible || got.Zone != "7" || got.Cell != (domain.Cell{X: 1, Z: 4}) {
		t.Fatal(got)
	}
	if got := plants[1]; got.ID != "Thing_Plant_Rice12" || got.Designated || got.Zone != "" {
		t.Fatal(got)
	}
	p.Cells = nil
	if plants, known := blightCensus(p).Value(); !known || len(plants) != 0 {
		t.Fatal("an observed window with no blight is the settled state", plants, known)
	}
	p.Region = policy.Rectangle{}
	if _, known := blightCensus(p).Value(); known {
		t.Fatal("no window withholds the census")
	}
	p.Region, p.Zones.Complete = policy.Rectangle{Width: 20, Height: 20}, false
	if _, known := blightCensus(p).Value(); known {
		t.Fatal("no zone section withholds the census")
	}
}
