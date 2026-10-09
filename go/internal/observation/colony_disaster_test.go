package observation

import (
	"errors"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// heads is a frame whose things table holds a head-only row for each of
// refs.
func heads(refs ...*o.EntityRef) bridge.Tables {
	things := bridge.Things{}
	for _, ref := range refs {
		things = things.With(ref.GetId(), &o.Thing{Thing: ref})
	}
	return bridge.Tables{Things: things}
}

// buildingRows is the building table of rows.
func buildingRows(rows ...*o.BuildingState) bridge.Buildings {
	return bridge.BuildingTable(&o.BuildingsSnapshot{Buildings: rows})
}

func TestColonyDisasterPreservesServiceUnknowns(t *testing.T) {
	v := &o.ColonyFactsSnapshot{Context: &c.ObservationContext{Tick: proto.Int64(12)}, Environment: []*o.EnvironmentCondition{{Id: proto.String("1"), DefName: proto.String("ColdSnap")}}}
	f := policy.RoundsFacts{}
	colonyDisasterTest(t, v, &f, bridge.Buildings{})
	if rows, k := f.DisasterConditions.Value(); !k || len(rows) != 1 || f.DisasterTick != 12 {
		t.Fatal(f)
	}
	if _, k := f.RecoveryBuildings.Value(); k {
		t.Fatal("missing census became empty")
	}
	b := &o.BuildingState{Building: &o.EntityRef{Id: proto.String("wall")}, UsesHitPoints: proto.Bool(true), HitPoints: proto.Int32(40), MaxHitPoints: proto.Int32(100), Burning: proto.Bool(false), Settings: &o.BuildingSettings{Forbidden: proto.Bool(false)}, Service: &o.BuildingServiceState{BrokenDown: proto.Bool(false)}}
	v.Recovery = &o.RecoveryReply{Outcome: &o.RecoveryReply_Observed{Observed: &o.RecoverySnapshot{Buildings: []*c.Ref{bridge.NewRef(b.Building.GetId())}}}}
	table := buildingRows(b)
	f = policy.RoundsFacts{}
	colonyDisasterTest(t, v, &f, bridge.Buildings{})
	if _, k := f.RecoveryBuildings.Value(); k {
		t.Fatal("an unresolved building reference became a known census")
	}
	f = policy.RoundsFacts{}
	colonyDisasterTest(t, v, &f, table)
	pending, err := policy.RecoveryPending(f.RecoveryBuildings)
	if _, k := pending.Value(); err != nil || k {
		t.Fatal("missing fuel component evidence recovered", err)
	}
	b.Service.Issues = []*o.ReadIssue{{Field: proto.String("fuel"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}}
	f = policy.RoundsFacts{}
	colonyDisasterTest(t, v, &f, table)
	pending, err = policy.RecoveryPending(f.RecoveryBuildings)
	rows, k := pending.Value()
	if err != nil || !k || len(rows) != 1 || rows[0].Method != policy.RecoveryRepair {
		t.Fatal(rows, k, err)
	}
	v.Environment = nil
	v.Issues = []*o.ReadIssue{{Field: proto.String("environment")}}
	f = policy.RoundsFacts{}
	colonyDisasterTest(t, v, &f, bridge.Buildings{})
	if _, k := f.DisasterConditions.Value(); k {
		t.Fatal("missing environment became clear")
	}
}

func TestColonyDisasterCarriesRemainingTicks(t *testing.T) {
	v := &o.ColonyFactsSnapshot{Context: &c.ObservationContext{Tick: proto.Int64(12)}, Environment: []*o.EnvironmentCondition{
		{Id: proto.String("1"), DefName: proto.String("ToxicFallout"), TicksLeft: proto.Int64(90000)},
		{Id: proto.String("2"), DefName: proto.String("VolcanicWinter"), Permanent: proto.Bool(true), TicksLeft: proto.Int64(5)},
		{Id: proto.String("3"), DefName: proto.String("ColdSnap"), TicksLeft: proto.Int64(-1)},
		{Id: proto.String("4"), DefName: proto.String("Eclipse")},
	}}
	f := policy.RoundsFacts{}
	colonyDisasterTest(t, v, &f, bridge.Buildings{})
	rows, k := f.DisasterConditions.Value()
	if !k || len(rows) != 4 {
		t.Fatal(rows, k)
	}
	if left, known := rows[0].RemainingTicks().Value(); !known || left != 90000 || rows[0].Permanent {
		t.Fatal("timed condition lost its remaining ticks", rows[0])
	}
	if _, known := rows[1].RemainingTicks().Value(); known || !rows[1].Permanent {
		t.Fatal("permanent condition kept a duration", rows[1])
	}
	for _, row := range rows[2:] {
		if _, known := row.RemainingTicks().Value(); known {
			t.Fatal("unreported or negative duration became known", row)
		}
	}
	if _, err := policy.ReviewDisaster(f.DisasterConditions, domain.Unknown[[]policy.RecoveryBuilding](), nil, nil, 12); err != nil {
		t.Fatal(err)
	}
}

// colonyDisasterTest runs colonyDisaster against a catalog with the Core
// condition rows.
func colonyDisasterTest(t *testing.T, v *o.ColonyFactsSnapshot, f *policy.RoundsFacts, buildings bridge.Buildings) {
	t.Helper()
	conditions, known, err := colonyConditions(v, decodeCatalog(t, &o.DefinitionCatalog{ThingDefs: []*d.ThingDef{{DefName: "Anchor"}}, StatValues: &o.DefStatTable{}}))
	if err != nil {
		t.Fatal(err)
	}
	colonyDisaster(v, f, buildings, conditions, known)
}

// TestColonyConditionsReadTheDefRow: the power outage is the def's class, not
// a name, and a condition without a row is an error.
func TestColonyConditionsReadTheDefRow(t *testing.T) {
	catalog := decodeCatalog(t, &o.DefinitionCatalog{ThingDefs: []*d.ThingDef{{DefName: "Anchor"}}, StatValues: &o.DefStatTable{}})
	v := &o.ColonyFactsSnapshot{Environment: []*o.EnvironmentCondition{{Id: proto.String("1"), DefName: proto.String("SolarFlare")}, {Id: proto.String("2"), DefName: proto.String("ColdSnap")}}}
	rows, known, err := colonyConditions(v, catalog)
	if err != nil || !known || len(rows) != 2 || !rows[0].DisablesPower || rows[1].DisablesPower {
		t.Fatal(rows, known, err)
	}
	v.Environment = []*o.EnvironmentCondition{{Id: proto.String("3"), DefName: proto.String("ModdedStorm")}}
	if _, _, err := colonyConditions(v, catalog); err == nil {
		t.Fatal("a condition with no catalog row was accepted")
	}
}

// TestColonyOutdoorsDarkReadsTheBiomeConditions: darkness is the
// biome's map conditions' class family; no biome read, no catalog and a
// biome without a row are each a named error.
func TestColonyOutdoorsDarkReadsTheBiomeConditions(t *testing.T) {
	catalog := decodeCatalog(t, &o.DefinitionCatalog{ThingDefs: []*d.ThingDef{{DefName: "Anchor"}}, StatValues: &o.DefStatTable{}})
	for biome, want := range map[string]bool{"FixtureDarkBiome": true, "FixtureLitBiome": false} {
		got, err := colonyOutdoorsDark(&o.ColonyFactsSnapshot{Biome: proto.String(biome)}, catalog)
		if dark, known := got.Value(); err != nil || !known || dark != want {
			t.Fatal(biome, got, err)
		}
	}
	if got, err := colonyOutdoorsDark(&o.ColonyFactsSnapshot{}, catalog); !errors.Is(err, policy.ErrOutdoorsDarkUnknown) || !strings.Contains(err.Error(), "no biome") || isKnown(got) {
		t.Fatal("no biome read did not fail loudly", got, err)
	}
	if got, err := colonyOutdoorsDark(&o.ColonyFactsSnapshot{Biome: proto.String("FixtureDarkBiome")}, nil); !errors.Is(err, policy.ErrOutdoorsDarkUnknown) || !strings.Contains(err.Error(), "no definition catalog") || isKnown(got) {
		t.Fatal("no catalog did not fail loudly", got, err)
	}
	if _, err := colonyOutdoorsDark(&o.ColonyFactsSnapshot{Biome: proto.String("Unlisted")}, catalog); err == nil {
		t.Fatal("a biome with no row was accepted")
	}
}

func isKnown(f domain.Fact[bool]) bool { _, ok := f.Value(); return ok }
