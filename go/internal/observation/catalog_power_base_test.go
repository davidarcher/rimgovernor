package observation

import (
	"maps"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// The base wattage a power row states is the def's, not the frame's (#1726):
// for every power building of the recorded catalog the catalog-derived value
// equals what the retired native base_w carried, -CompProperties_Power
// .PowerConsumption of the building's comp, which is the base draw times the
// factor of each power upgrade whose research is finished. The oracle reads the
// comp through its typed rows and applies the game's own loop.
func TestRecordedCatalogPowerBaseWMatchesTheRetiredNativeValue(t *testing.T) {
	catalog := recordedCatalog(t)
	upgrades := map[string]bool{}
	for _, row := range catalog.ThingDefs {
		if p := powerComp(row); p != nil {
			for _, u := range p.upgrades {
				upgrades[u.GetValue().GetResearchProject()] = true
			}
		}
	}
	if len(upgrades) == 0 {
		t.Fatal("the recorded catalog carries no power upgrade to compare")
	}
	finishedSets := map[string][]string{"none": {}, "all": slices.Sorted(maps.Keys(upgrades))}
	for project := range upgrades {
		finishedSets[project] = []string{project}
	}
	buildings := 0
	for _, name := range slices.Sorted(maps.Keys(catalog.ThingDefs)) {
		p := powerComp(catalog.ThingDefs[name])
		if p == nil {
			if _, err := catalog.PowerBaseW(name, domain.Known([]string{})); err == nil {
				t.Errorf("%s has no power comp and still stated a base wattage", name)
			}
			continue
		}
		buildings++
		for set, finished := range finishedSets {
			want := p.base
			for _, u := range p.upgrades {
				if project := u.GetValue().GetResearchProject(); project != "" && slices.Contains(finished, project) {
					want *= u.GetValue().GetFactor()
				}
			}
			got, err := catalog.PowerBaseW(name, domain.Known(finished))
			if err != nil {
				t.Fatalf("%s: %v", name, err)
			}
			if value, ok := got.Value(); !ok || value != float64(0-want) {
				t.Errorf("%s with %s finished: base_w %v (known %v), the game's -PowerConsumption is %v", name, set, value, ok, 0-want)
			}
		}
		unknown, err := catalog.PowerBaseW(name, domain.Unknown[[]string]())
		if err != nil {
			t.Fatal(name, err)
		}
		if _, ok := unknown.Value(); ok != (len(p.upgrades) == 0) {
			t.Errorf("%s: base_w known %v without a research census, with %d upgrades", name, ok, len(p.upgrades))
		}
	}
	if buildings < 30 {
		t.Fatalf("only %d power defs in the recorded catalog", buildings)
	}
	// The stock numbers a power budget stands on, from the game's XML.
	for name, want := range map[string]float64{"Battery": 0, "SolarGenerator": 1700, "WoodFiredGenerator": 1000, "Cooler": -200, "StandingLamp": -30} {
		got, err := catalog.PowerBaseW(name, domain.Known([]string{}))
		if value, ok := got.Value(); err != nil || !ok || value != want {
			t.Errorf("%s: base_w %v (known %v, %v), want %v", name, value, ok, err, want)
		}
	}
	lamp, err := catalog.PowerBaseW("StandingLamp", domain.Known([]string{"ColoredLights"}))
	if value, ok := lamp.Value(); err != nil || !ok || value != -15 {
		t.Errorf("a lamp with ColoredLights finished draws %v (known %v, %v), want 15 W", value, ok, err)
	}
}

type powerCompRow struct {
	base     float32
	upgrades []*d.Opt_CompProperties_Power_PowerUpgrade
}

// powerComp is the typed CompProperties_Power (or battery) comp of row, nil
// without one; a def with two such comps panics because the game reads the
// building's own comp, not the first of several.
func powerComp(row *d.ThingDef) *powerCompRow {
	var found []*powerCompRow
	for _, comp := range row.GetComps() {
		if p := comp.GetValue().GetCompProperties_Power(); p != nil {
			found = append(found, &powerCompRow{base: p.GetBasePowerConsumption(), upgrades: p.GetPowerUpgrades()})
		}
		if b := comp.GetValue().GetCompProperties_Battery(); b != nil {
			found = append(found, &powerCompRow{base: b.GetBasePowerConsumption(), upgrades: b.GetPowerUpgrades()})
		}
	}
	if len(found) > 1 {
		panic(row.GetDefName() + " has two power comps")
	}
	if len(found) == 0 {
		return nil
	}
	return found[0]
}

// The flooring census prices every terrain row of the catalog (#1726), which
// holds every terrain a cell can stand on: the table native used to name was
// the subset under the rooms' and routes' cells, so each terrain it could have
// sent is priced here with the identical stat, path cost and natural values.
// (The natural flag is also pinned by TestCatalogFloorTerrain.)
func TestRecordedCatalogPricesEveryTerrainNativeCouldName(t *testing.T) {
	catalog := recordedCatalog(t)
	table, err := catalog.FloorTerrains()
	if err != nil {
		t.Fatal(err)
	}
	if len(table) != len(catalog.TerrainDefs) || len(table) < 100 {
		t.Fatalf("%d priced terrains for %d rows", len(table), len(catalog.TerrainDefs))
	}
	for name, row := range catalog.TerrainDefs {
		old, err := catalog.FloorTerrain(name)
		if err != nil || table[name] != old {
			t.Fatalf("%s: table %+v, per-terrain %+v (%v)", name, table[name], old, err)
		}
		if old.PathCost != row.GetPathCost() || old.Natural != row.GetNatural() {
			t.Fatalf("%s: %+v disagrees with its row", name, old)
		}
	}
	observation := policy.FlooringObservation{Terrains: table}
	if err := observation.Validate(); err != nil {
		t.Fatal(err)
	}
}
