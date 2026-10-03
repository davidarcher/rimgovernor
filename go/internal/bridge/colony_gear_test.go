package bridge

import (
	"fmt"
	"math"
	"testing"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func gearColonyFixture(t *testing.T) *o.ColonyFactsSnapshot {
	v := colonyFixture(t).GetObserved()
	ctx := v.Context
	p := &o.GearLoadout{Pawn: &c.Ref{Id: proto.String("pawn")}, Snapshot: &o.SnapshotRef{Context: proto.Clone(ctx).(*c.ObservationContext), EntityId: proto.String("pawn"), Token: proto.String("loadout")}, Candidates: []*o.GearCandidate{{Gain: proto.Float64(.3), Item: &o.GearItem{Thing: &c.Ref{Id: proto.String("parka")}}}}, Gender: d.Gender_GENDER_FEMALE.Enum(), DevelopmentalStage: d.DevelopmentalStage_DEVELOPMENTAL_STAGE_ADULT.Enum(), BodyPartGroups: []string{"Torso"}, ComfortableMinC: proto.Float64(10), ComfortableMaxC: proto.Float64(30)}
	p.LoadoutModel = &o.GearLoadoutModel{Options: []*o.GearLoadoutOption{{Id: proto.String("bill:Apparel_FlakVest/"), DefName: proto.String("Apparel_FlakVest"), Quality: proto.Int32(2), Source: proto.String("bill"), Condition: proto.Float64(1), Research: []string{"FlakArmor"}, Ingredients: []*o.Quantity{{DefName: proto.String("Steel"), Units: proto.Int64(60)}}}}}
	v.GetPlanning().GetObserved().Gear = &o.GearSnapshot{Context: proto.Clone(ctx).(*c.ObservationContext), Pawns: []*o.GearLoadout{p}}
	return v
}

func TestColonyGearRequiresExactCompleteLoadoutEvidence(t *testing.T) {
	v := gearColonyFixture(t)
	if err := ValidateColonyFacts(v, v.Context.Identity); err != nil {
		t.Fatal(err)
	}
	for name, change := range map[string]func(*o.GearSnapshot){
		"stale census":       func(g *o.GearSnapshot) { g.Context.Tick = proto.Int64(g.Context.GetTick() - 1) },
		"stale loadout":      func(g *o.GearSnapshot) { g.Pawns[0].Snapshot.Context.Tick = proto.Int64(g.Context.GetTick() - 1) },
		"changed generation": func(g *o.GearSnapshot) { g.Pawns[0].Snapshot.Context.NativeGeneration = proto.Uint64(99) },
		"other pawn token":   func(g *o.GearSnapshot) { g.Pawns[0].Snapshot.EntityId = proto.String("other") },
		"blocked eligible":   func(g *o.GearSnapshot) { g.Pawns[0].Blocker = proto.String("player job") },
		"nan gain":           func(g *o.GearSnapshot) { g.Pawns[0].Candidates[0].Gain = proto.Float64(math.NaN()) },
		"no wearer stage":    func(g *o.GearSnapshot) { g.Pawns[0].DevelopmentalStage = nil },
		"mixed wearer stage": func(g *o.GearSnapshot) { g.Pawns[0].DevelopmentalStage = d.DevelopmentalStage(12).Enum() },
		"no wearer gender":   func(g *o.GearSnapshot) { g.Pawns[0].Gender = nil },
		"wearer group":       func(g *o.GearSnapshot) { g.Pawns[0].BodyPartGroups = []string{"Torso", "Torso"} },
		"model source": func(g *o.GearSnapshot) {
			g.Pawns[0].LoadoutModel.Options[0].Source = proto.String("trade")
		},
		"model nan condition": func(g *o.GearSnapshot) {
			g.Pawns[0].LoadoutModel.Options[0].Condition = proto.Float64(math.NaN())
		},
		"model empty ingredient": func(g *o.GearSnapshot) {
			g.Pawns[0].LoadoutModel.Options[0].Ingredients[0].Units = proto.Int64(0)
		},
		"no loadout model": func(g *o.GearSnapshot) { g.Pawns[0].LoadoutModel = nil },
		"no comfort range": func(g *o.GearSnapshot) { g.Pawns[0].ComfortableMinC = nil },
	} {
		t.Run(name, func(t *testing.T) {
			v := gearColonyFixture(t)
			change(v.GetPlanning().GetObserved().Gear)
			if err := ValidateColonyFacts(v, v.Context.Identity); err == nil {
				t.Fatal("invalid gear evidence accepted")
			}
		})
	}
	// Candidates are every eligible item; a list past the old 256 cap is
	// accepted whole.
	v = gearColonyFixture(t)
	full := v.GetPlanning().GetObserved().Gear.Pawns[0]
	for len(full.Candidates) < 300 {
		row := proto.Clone(full.Candidates[0]).(*o.GearCandidate)
		row.Item.Thing.Id = proto.String(fmt.Sprintf("parka-%d", len(full.Candidates)))
		full.Candidates = append(full.Candidates, row)
	}
	if err := ValidateColonyFacts(v, v.Context.Identity); err != nil {
		t.Fatal(err)
	}
	v = gearColonyFixture(t)
	planning := v.GetPlanning().GetObserved()
	planning.Issues = []*o.ReadIssue{{Field: proto.String("gear"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_READ_FAILED.Enum()}}}
	if err := ValidateColonyFacts(v, v.Context.Identity); err == nil {
		t.Fatal("gear both known and unavailable")
	}
	planning.Gear = nil
	if err := ValidateColonyFacts(v, v.Context.Identity); err != nil {
		t.Fatal(err)
	}
}

func TestGearStorageCensusValidation(t *testing.T) {
	for _, bad := range []string{"", "count", "quality", "band", "duplicate"} {
		v := gearColonyFixture(t)
		g := v.GetPlanning().GetObserved().Gear
		row := &o.GearStock{DefName: proto.String("Parka"), Stuff: proto.String("Cloth"), Quality: proto.Int32(2), HpBand: proto.Int32(9), Count: proto.Int32(3)}
		g.StoredApparel = &o.GearStorage{Rows: []*o.GearStock{row}}
		switch bad {
		case "count":
			row.Count = nil
		case "quality":
			row.Quality = proto.Int32(7)
		case "band":
			row.HpBand = proto.Int32(10)
		case "duplicate":
			g.StoredApparel.Rows = append(g.StoredApparel.Rows, row)
		}
		err := ValidateColonyFacts(v, v.Context.Identity)
		if (err != nil) != (bad != "") {
			t.Fatal(bad, err)
		}
	}
}

func TestGearClimateWireValidation(t *testing.T) {
	for name, mutate := range map[string]func(*o.GearSnapshot){
		"valid":            func(g *o.GearSnapshot) {},
		"short curve":      func(g *o.GearSnapshot) { g.OutdoorTemperatureByTwelfthC = g.OutdoorTemperatureByTwelfthC[:11] },
		"nan":              func(g *o.GearSnapshot) { g.OutdoorTemperatureByTwelfthC[0] = float32(math.NaN()) },
		"missing phase":    func(g *o.GearSnapshot) { g.CurrentTwelfth = nil },
		"invalid phase":    func(g *o.GearSnapshot) { g.CurrentTwelfth = proto.Uint32(12) },
		"missing boundary": func(g *o.GearSnapshot) { g.TicksToNextTwelfth = nil },
		"missing duration": func(g *o.GearSnapshot) { g.ActiveWeather.RemainingTicks = nil },
		"invalid duration": func(g *o.GearSnapshot) { g.ActiveWeather.RemainingTicks = proto.Int64(-2) },
		"wrong sign":       func(g *o.GearSnapshot) { g.ActiveWeather.TemperatureOffsetC = proto.Float32(20) },
		"unknown weather":  func(g *o.GearSnapshot) { g.ActiveWeather.DefName = proto.String("Rain") },
	} {
		t.Run(name, func(t *testing.T) {
			v := gearColonyFixture(t)
			g := v.GetPlanning().GetObserved().Gear
			g.OutdoorTemperatureByTwelfthC = make([]float32, 12)
			g.CurrentTwelfth, g.TicksToNextTwelfth = proto.Uint32(7), proto.Int32(300000)
			g.ActiveWeather = &o.GearWeatherCondition{DefName: proto.String("ColdSnap"), RemainingTicks: proto.Int64(60000), TemperatureOffsetC: proto.Float32(-20)}
			mutate(g)
			err := ValidateColonyFacts(v, v.Context.Identity)
			if (err == nil) != (name == "valid") {
				t.Fatalf("validation: %v", err)
			}
		})
	}
}
