package observation

import (
	"os"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

// A recorded Odyssey colony read projects conditions, hazard terrain, lava
// emergences and underground sites, keeps absent scalars unknown, and an
// absent or unavailable section stays unknown.
func TestOdysseyColonyProjection(t *testing.T) {
	data, err := os.ReadFile("../../../contracts/fixtures/colony-core.json")
	if err != nil {
		t.Fatal(err)
	}
	r := &o.ColonyFactsReply{}
	if err = protojson.Unmarshal(data, r); err != nil {
		t.Fatal(err)
	}
	id := Identity{Colony: "colony", Load: "load", Map: 0, Tick: 7, NativeGeneration: domain.Known(domain.NativeGeneration(1))}
	decode := func() ColonyProjection {
		t.Helper()
		p, err := DecodeColony(r, id, bridge.Tables{})
		if err != nil {
			t.Fatal(err)
		}
		return p
	}
	for _, section := range []*o.OdysseySection{nil, {Outcome: &o.OdysseySection_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_READ_FAILED.Enum(), Detail: proto.String("unreadable")}}}} {
		r.GetObserved().Odyssey = section
		if _, known := decode().Odyssey.Value(); known {
			t.Fatal("absent or unavailable section became known")
		}
	}
	f := &o.OdysseyColonyFacts{
		Conditions: []*o.ActiveCondition{
			{ConditionId: proto.String("GameCondition_1"), DefName: proto.String("LavaFlow"), ConditionClass: proto.String("RimWorld.GameCondition_LavaFlow"), TicksPassed: proto.Int32(10), TicksLeft: proto.Int32(500), Permanent: proto.Bool(false)},
			{ConditionId: proto.String("GameCondition_2"), DefName: proto.String("ToxicSpewer"), ConditionClass: proto.String("RimWorld.GameCondition_ToxicFallout"), Permanent: proto.Bool(true), CauserId: proto.String("Building_7")}},
		HazardTerrain:  []*o.HazardTerrain{{DefName: proto.String("LavaDeep"), Cells: proto.Uint32(40), Dangerous: proto.Bool(true), BurnDamage: proto.Int32(3), HeatPerTick: proto.Float64(0.5)}},
		LavaEmergences: []*o.LavaEmergenceState{{ThingId: proto.String("LavaEmergence_3"), Position: cell(20, 20)}},
		Sites: []*o.UndergroundSite{{HatchId: proto.String("AncientHatch_4"), PocketMapId: proto.Int32(2), StockpileType: proto.String("Medical"), ColonistsPresent: proto.Uint32(2),
			Hackables: []*o.UndergroundHackable{
				{ThingId: proto.String("terminal"), DefName: proto.String("AncientTerminal"), Position: cell(5, 6), ProgressPercent: proto.Float64(0.25), Hacked: proto.Bool(false), LockedOut: proto.Bool(true)},
				{ThingId: proto.String("door"), DefName: proto.String("AncientDoor"), Position: cell(7, 6)}}}},
	}
	r.GetObserved().Odyssey = &o.OdysseySection{Outcome: &o.OdysseySection_Observed{Observed: f}}
	v, known := decode().Odyssey.Value()
	if !known || len(v.Conditions) != 2 || len(v.HazardTerrain) != 1 || len(v.LavaEmergences) != 1 || len(v.Sites) != 1 || len(v.Sites[0].Hackables) != 2 {
		t.Fatalf("lost rows: %+v", v)
	}
	if _, ok := v.Conditions[1].TicksLeft.Value(); ok || !v.Conditions[1].Permanent {
		t.Fatal("permanent condition reports ticks left")
	}
	if n, ok := v.Conditions[0].TicksLeft.Value(); !ok || n != 500 {
		t.Fatal("ticks left lost")
	}
	if _, ok := v.Sites[0].Layout.Value(); ok {
		t.Fatal("absent layout became known")
	}
	if _, ok := v.Sites[0].Hackables[1].ProgressPercent.Value(); ok {
		t.Fatal("absent progress became known")
	}
	if l, ok := v.Sites[0].Hackables[0].LockedOut.Value(); !ok || !l {
		t.Fatal("lockout lost")
	}
	// Contract violations are refused, never projected.
	for name, change := range map[string]func(){
		"duplicate condition":  func() { f.Conditions[1].ConditionId = proto.String("GameCondition_1") },
		"permanent with ticks": func() { f.Conditions[1].TicksLeft = proto.Int32(5) },
		"negative ticks":       func() { f.Conditions[0].TicksLeft = proto.Int32(-1) },
		"duplicate terrain":    func() { f.HazardTerrain = append(f.HazardTerrain, f.HazardTerrain[0]) },
		"empty terrain":        func() { f.HazardTerrain[0].Cells = proto.Uint32(0) },
		"off map emergence":    func() { f.LavaEmergences[0].Position = cell(-1, 0) },
		"duplicate pocket map": func() {
			f.Sites = append(f.Sites, &o.UndergroundSite{HatchId: proto.String("other"), PocketMapId: proto.Int32(2)})
		},
		"progress above one": func() { f.Sites[0].Hackables[0].ProgressPercent = proto.Float64(1.5) },
		"duplicate hackable": func() { f.Sites[0].Hackables[1].ThingId = proto.String("terminal") },
	} {
		saved := proto.Clone(f).(*o.OdysseyColonyFacts)
		change()
		if _, err := DecodeColony(r, id, bridge.Tables{}); err == nil {
			t.Fatalf("%s accepted", name)
		}
		proto.Reset(f)
		proto.Merge(f, saved)
	}
}
