package observation

import (
	"compress/gzip"
	"io"
	"os"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// recordedCatalog is a definition catalog recorded from a running game: the
// old native planning rows beside the def rows and stat table the views read.
// RG_CATALOG names an uncompressed full dump instead of the committed one.
func recordedCatalog(t *testing.T) (*o.DefinitionCatalog, *bridge.DefinitionCatalog) {
	t.Helper()
	var data []byte
	if path := os.Getenv("RG_CATALOG"); path != "" {
		var err error
		if data, err = os.ReadFile(path); err != nil {
			t.Fatal(err)
		}
	} else {
		file, err := os.Open("testdata/planning_catalog.pb.gz")
		if err != nil {
			t.Fatal(err)
		}
		defer file.Close()
		zr, err := gzip.NewReader(file)
		if err != nil {
			t.Fatal(err)
		}
		if data, err = io.ReadAll(zr); err != nil {
			t.Fatal(err)
		}
	}
	wire := &o.DefinitionCatalog{}
	if err := proto.Unmarshal(data, wire); err != nil {
		t.Fatal(err)
	}
	catalog, err := bridge.DecodeDefinitionCatalog(wire, wire.GetContext().GetIdentity())
	if err != nil {
		t.Fatal(err)
	}
	return wire, catalog
}

// TestPlanningViewsMatchTheRecordedNativeRows compares every row the retired
// native planning writer produced with the view over the def rows and stat
// table, on a catalog recorded from the game (#1731).
func TestPlanningViewsMatchTheRecordedNativeRows(t *testing.T) {
	wire, catalog := recordedCatalog(t)
	if len(wire.Definitions) < 100 {
		t.Fatalf("recorded catalog holds %d planning rows", len(wire.Definitions))
	}
	things, terrains, stuffed := 0, 0, 0
	for _, old := range wire.Definitions {
		name := old.GetDefinition().GetDefName()
		view, listed, err := planningView(catalog, name)
		if err != nil || !listed {
			t.Errorf("%s: view listed=%v err=%v", name, listed, err)
			continue
		}
		fail := func(field string, old, new any) { t.Errorf("%s.%s: native %v, view %v", name, field, old, new) }
		sameFloat := func(field string, native *float64, got domain.Fact[float64]) {
			v, known := got.Value()
			if native == nil && known {
				fail(field, "unset", v)
			} else if native != nil && (!known || v != *native) {
				fail(field, *native, got)
			}
		}
		sameBool := func(field string, native *bool, got domain.Fact[bool]) {
			v, known := got.Value()
			if native == nil && known {
				fail(field, "unset", v)
			} else if native != nil && (!known || v != *native) {
				fail(field, *native, got)
			}
		}
		if !slices.Equal(old.GetResearchPrerequisites(), view.Research) {
			fail("Research", old.GetResearchPrerequisites(), view.Research)
		}
		if skill, known := view.ConstructionSkill.Value(); !known || skill != old.GetConstructionSkill() {
			fail("ConstructionSkill", old.GetConstructionSkill(), view.ConstructionSkill)
		}
		if size, known := view.Size.Value(); !known || size.Width != int32(old.GetSize().GetWidth()) || size.Height != int32(old.GetSize().GetHeight()) {
			fail("Size", old.GetSize(), view.Size)
		}
		sameBool("NeedsPower", old.NeedsPower, view.NeedsPower)
		sameFloat("PowerW", old.PowerW, view.PowerW)
		sameFloat("GlowRadius", old.GlowRadius, view.GlowRadius)
		sameFloat("ExplosiveRadius", old.ExplosiveRadius, view.ExplosiveRadius)
		sameBool("MechCharger", old.MechCharger, view.MechCharger)
		sameBool("Pollutes", old.Pollutes, view.Pollutes)
		sameBool("Terrain", old.Terrain, view.Terrain)
		sameFloat("GrowerFertility", old.GrowerFertility, view.GrowerFertility)
		if tag, known := view.SowTag.Value(); (old.SowTag == nil) == known || known && tag != old.GetSowTag() {
			fail("SowTag", old.SowTag, view.SowTag)
		}
		if !slices.Equal(old.GetRoomRoles(), view.RoomRoles) {
			fail("RoomRoles", old.GetRoomRoles(), view.RoomRoles)
		}
		sameFloat("HarvestWork", old.HarvestWork, view.HarvestWork)
		sameFloat("GrowDays", old.GrowDays, view.GrowDays)
		sameFloat("FertilityMin", old.FertilityMin, view.FertilityMin)
		sameFloat("FertilitySensitivity", old.FertilitySensitivity, view.FertilitySensitivity)
		sameFloat("GrowMinGlow", old.GrowMinGlow, view.GrowMinGlow)
		sameFloat("HarvestNutrition", old.HarvestNutrition, view.HarvestNutrition)
		sameBool("RawPreferred", old.RawPreferred, view.RawPreferred)
		sameBool("Edible", old.Edible, view.Edible)
		sameBool("RequiresPollution", old.RequiresPollution, view.RequiresPollution)
		sameBool("RequiresCleanSoil", old.RequiresCleanSoil, view.RequiresCleanSoil)
		if tags, known := view.SowTags.Value(); known != (old.GrowDays != nil) || known && !slices.Equal(tags, old.GetSowTags()) {
			fail("SowTags", old.GetSowTags(), view.SowTags)
		}
		sameFloat("Cleanliness", old.Cleanliness, view.Cleanliness)
		sameFloat("Beauty", old.Beauty, view.Beauty)
		sameFloat("Flammability", old.Flammability, view.Flammability)
		if path, known := view.PathCost.Value(); (old.PathCost == nil) == known || known && path != old.GetPathCost() {
			fail("PathCost", old.PathCost, view.PathCost)
		}
		if old.GetTerrain() {
			terrains++
		} else {
			things++
		}
		if len(old.StuffOptions) == 0 {
			if view.Stuffed || len(view.StuffOptions) != 0 {
				fail("Stuffed", false, view.StuffOptions)
			}
			if old.Stuff != nil {
				fail("Stuff", old.GetStuff(), "none")
			}
			// A crop is never built: the native row carried the stat's default
			// of 1, which nothing reads.
			if old.GrowDays == nil {
				sameFloat("WorkToBuild", old.WorkToBuild, view.WorkToBuild)
			}
			costs, known := view.Costs.Value()
			if known != !hasIssue(old.Issues, "costs") || known && !sameAmounts(old.Costs, costs) {
				fail("Costs", old.Costs, view.Costs)
			}
			continue
		}
		stuffed++
		if !view.Stuffed || len(view.StuffOptions) != len(old.StuffOptions) {
			fail("StuffOptions", len(old.StuffOptions), len(view.StuffOptions))
			continue
		}
		if _, known := view.Costs.Value(); known {
			fail("Costs", "unset for a stuffed def", view.Costs)
		}
		for i, option := range old.StuffOptions {
			got := view.StuffOptions[i]
			if got.Stuff != option.GetStuff() || !sameAmounts(option.Costs, got.Costs) {
				fail("StuffOptions", option, got)
			}
			// The wood-first row's own stuff: its costs, work and rest
			// effectiveness are the option's.
			if option.GetStuff() != old.GetStuff() {
				continue
			}
			if work, shown := got.Stats[bridge.StatWorkToBuild]; old.WorkToBuild != nil && (!shown || work != old.GetWorkToBuild()) {
				fail("WorkToBuild at "+got.Stuff, old.GetWorkToBuild(), got.Stats)
			}
			// The stat table shows rest effectiveness for beds only; the native
			// row read the stat's 0.4 default for every other def.
			if rest, shown := got.Stats[bridge.StatBedRestEffectiveness]; old.RestEffectiveness != nil && (shown && rest != old.GetRestEffectiveness() || !shown && float32(old.GetRestEffectiveness()) != 0.4) {
				fail("RestEffectiveness at "+got.Stuff, old.GetRestEffectiveness(), got.Stats)
			}
		}
	}
	if things == 0 || terrains == 0 || stuffed == 0 {
		t.Fatalf("recorded catalog compared %d things, %d terrains, %d stuffed", things, terrains, stuffed)
	}
	t.Logf("compared %d things (%d stuffed) and %d terrains", things, stuffed, terrains)
}

func sameAmounts(native []*o.Quantity, got []policy.Amount) bool {
	if len(native) != len(got) {
		return false
	}
	for i, q := range native {
		if string(got[i].Resource) != q.GetDefName() || got[i].Count != q.GetUnits() {
			return false
		}
	}
	return true
}
