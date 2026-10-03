package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"strings"
	"testing"

	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

func platformComp(factor float32) *d.CompPropertiesAny {
	return &d.CompPropertiesAny{Value: &d.CompPropertiesAny_CompProperties_EntityHolderPlatform{CompProperties_EntityHolderPlatform: &d.CompProperties_EntityHolderPlatform{ContainmentFactor: factor}}}
}

func containmentCatalog() *o.DefinitionCatalog {
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
	v.Defs = &d.DefSets{StatDefs: []*d.StatDef{{DefName: "ContainmentStrength", DefaultBaseValue: 1}}}
	v.StatValues = &o.DefStatTable{
		Stats: []string{"MaxHitPoints"},
		Rows: []*o.DefStatRow{
			{DefName: "Wall", StuffName: "Steel", Stat: []int32{0}, Value: []float32{4000}},
			{DefName: "Door", StuffName: "Steel", Stat: []int32{0}, Value: []float32{300}},
		},
	}
	return v
}

// TestContainmentDefsReadTheFormulaInputsFromDefs (#1741): the holder is the
// platform def with the greatest factor ,
// its base falls back to the stat's default, wall and door hit points are the
// game's stat values and facilities are the linked defs' ContainmentStrength
// offsets with their limits.
func TestContainmentDefsReadTheFormulaInputsFromDefs(t *testing.T) {
	catalog, err := DecodeDefinitionCatalog(containmentCatalog(), pbIdentity())
	if err != nil {
		t.Fatal(err)
	}
	got, err := catalog.ContainmentDefs("Wall", "Steel", "Door", "Steel")
	if err != nil {
		t.Fatal(err)
	}
	if got.Holder != "HoldingPlatform" || got.HolderFactor != 1 || got.HolderBase != 2 || got.FloorStrength != 1 || got.WallHP != 4000 || got.DoorHP != 300 {
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
		"no stat def":  {func(v *o.DefinitionCatalog) { v.Defs.StatDefs[0].DefName = "MarketValue" }, "stat def"},
		"no platform":  {func(v *o.DefinitionCatalog) { v.ThingDefs = v.ThingDefs[:3] }, "no holding platform"},
		"no wall hp":   {func(v *o.DefinitionCatalog) { v.StatValues.Rows[0].Value[0] = 0 }, "no MaxHitPoints"},
		"no stat rows": {func(v *o.DefinitionCatalog) { v.StatValues.Rows = v.StatValues.Rows[1:] }, "no stat values"},
		"unknown link": {func(v *o.DefinitionCatalog) {
			v.ThingDefs[3].Comps[1].GetValue().GetCompProperties_AffectedByFacilities().LinkableFacilities = []string{"Gone"}
		}, "lacks"},
	} {
		v := containmentCatalog()
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
