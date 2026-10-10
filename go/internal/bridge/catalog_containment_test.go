package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"slices"
	"strings"
	"testing"

	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

func platformComp(factor float32) *d.CompPropertiesAny {
	return &d.CompPropertiesAny{Value: &d.CompPropertiesAny_CompProperties_EntityHolderPlatform{CompProperties_EntityHolderPlatform: &d.CompProperties_EntityHolderPlatform{ContainmentFactor: factor}}}
}

func containmentCatalog(t testing.TB) *o.DefinitionCatalog {
	v := catalogReply(authorityTestContext(7)).GetObserved()
	linked := &d.CompPropertiesAny{Value: &d.CompPropertiesAny_CompProperties_AffectedByFacilities{CompProperties_AffectedByFacilities: &d.CompProperties_AffectedByFacilities{LinkableFacilities: []string{"Inhibitor"}}}}
	facility := &d.CompPropertiesAny{Value: &d.CompPropertiesAny_CompProperties_Facility{CompProperties_Facility: &d.CompProperties_Facility{
		StatOffsets: []*d.Opt_StatModifier{{Value: &d.StatModifier{Stat: "ContainmentStrength", Value: 10}}, {Value: &d.StatModifier{Stat: "Beauty", Value: 3}}}, MaxDistance: 4, MaxSimultaneous: 2}}}
	v.ThingDefs = []*d.ThingDef{
		{DefName: "Wall"}, {DefName: "Door"}, {DefName: "Steel"},
		{DefName: "HoldingPlatform", StatBases: []*d.Opt_StatModifier{{Value: &d.StatModifier{Stat: "ContainmentStrength", Value: 2}}}, Comps: []*d.Opt_CompPropertiesAny{{Value: platformComp(1)}, {Value: linked}}},
		{DefName: "ZPlatform", Comps: []*d.Opt_CompPropertiesAny{{Value: platformComp(1)}}},
		{DefName: "Inhibitor", Comps: []*d.Opt_CompPropertiesAny{{Value: facility}}},
	}
	strength := func(value float32) []*d.Opt_StatModifier {
		return []*d.Opt_StatModifier{{Value: &d.StatModifier{Stat: "ContainmentStrength", Value: value}}}
	}
	v.TerrainDefs = []*d.TerrainDef{{DefName: "BioferritePlate", StatBases: strength(16)}, {DefName: "Soil"}, {DefName: "SteelTile", StatBases: strength(3)}}
	hitPoints := func(value float32) []*d.Opt_StatModifier {
		return []*d.Opt_StatModifier{{Value: &d.StatModifier{Stat: "MaxHitPoints", Value: value}}}
	}
	v.ThingDefs[0].StatBases, v.ThingDefs[1].StatBases = hitPoints(4000), hitPoints(300)
	v.ThingDefs[0].StuffCategories, v.ThingDefs[1].StuffCategories = []string{"Metallic"}, []string{"Metallic"}
	v.ThingDefs[2].StuffProps = &d.StuffProperties{Categories: []string{"Metallic"}}
	withStatSupport(t, v)
	for _, stat := range v.Defs.StatDefs {
		if stat.DefName == "ContainmentStrength" {
			stat.DefaultBaseValue = 1
		}
	}
	return v
}

// TestContainmentDefsReadTheFormulaInputsFromDefs: the holder is the
// platform def with the greatest factor ,
// its base falls back to the stat's default, wall and door hit points are the
// game's stat values and facilities are the linked defs' ContainmentStrength
// offsets with their limits.
func TestContainmentDefsReadTheFormulaInputsFromDefs(t *testing.T) {
	catalog, err := DecodeDefinitionCatalog(containmentCatalog(t), pbIdentity())
	if err != nil {
		t.Fatal(err)
	}
	got, err := catalog.ContainmentDefs("Wall", "Steel", "Door", "Steel")
	if err != nil {
		t.Fatal(err)
	}
	if got.Holder != "HoldingPlatform" || got.HolderFactor != 1 || got.HolderBase != 2 || got.FloorStrength != 1 || got.Floor != (policy.ContainmentFloor{Def: "BioferritePlate", Strength: 16}) || got.WallHP != 4000 || got.DoorHP != 300 {
		t.Fatalf("%+v", got)
	}
	if len(got.Facilities) != 1 || got.Facilities[0].Def != "Inhibitor" || got.Facilities[0].Offset != 10 || got.Facilities[0].MaxDistance != 4 || got.Facilities[0].MaxSimultaneous != 2 {
		t.Fatalf("facilities %+v", got.Facilities)
	}
	// 2 + (100 + 3000/9000*50 + 60 + 1) * 1 with no facility.
	if strength, err := got.Predict(policy.ContainmentRoom{}); err != nil || strength < 179.6 || strength > 179.7 {
		t.Fatalf("strength %v %v", strength, err)
	}
}

func TestContainmentDefsRefuseMissingInputs(t *testing.T) {
	for name, tc := range map[string]struct {
		mutate func(*o.DefinitionCatalog)
		reason string
	}{
		"no stat def": {func(v *o.DefinitionCatalog) {
			v.Defs.StatDefs = slices.DeleteFunc(v.Defs.StatDefs, func(s *d.StatDef) bool { return s.DefName == "ContainmentStrength" })
		}, "stat def"},
		"no platform": {func(v *o.DefinitionCatalog) { v.ThingDefs = v.ThingDefs[:3] }, "no holding platform"},
		"no wall def": {func(v *o.DefinitionCatalog) { v.ThingDefs = v.ThingDefs[1:] }, "Wall"},
		"unknown link": {func(v *o.DefinitionCatalog) {
			v.ThingDefs[3].Comps[1].GetValue().GetCompProperties_AffectedByFacilities().LinkableFacilities = []string{"Gone"}
		}, "lacks"},
	} {
		v := containmentCatalog(t)
		tc.mutate(v)
		catalog, err := DecodeDefinitionCatalog(v, pbIdentity())
		if err != nil {
			t.Fatalf("%s: decode %v", name, err)
		}
		if _, err := catalog.ContainmentDefs("Wall", "Steel", "Door", "Steel"); err == nil || !strings.Contains(err.Error(), tc.reason) {
			t.Fatalf("%s: %v", name, err)
		}
	}
	var none *DefinitionCatalog
	if _, err := none.ContainmentDefs("Wall", "", "Door", ""); err == nil {
		t.Fatal("nil catalog answered")
	}
}
