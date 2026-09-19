package bridge

import (
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"math"
	"testing"
)

func TestRecreationCensusBoundary(t *testing.T) {
	valid := func() *o.ComfortFacts {
		v := comfortWire().Comfort.GetObserved()
		v.Joy = &o.RecreationCensus{Kinds: []string{"Dexterity"}, Pawns: []*o.JoyTolerance{{Pawn: "p", Tolerance: []float64{.4}, Bored: []bool{true}}}, Methods: []*o.JoyBuildingMethod{{Definition: "ChessTable", Kind: "Cerebral"}}}
		return v
	}
	if err := validateRecreationCensus(valid(), map[string]bool{"p": true}); err != nil {
		t.Fatal(err)
	}
	for _, mutate := range []func(*o.ComfortFacts){
		func(v *o.ComfortFacts) { v.Joy.Kinds = append(v.Joy.Kinds, "Dexterity") },
		func(v *o.ComfortFacts) { v.Joy.Pawns[0].Tolerance = nil },
		func(v *o.ComfortFacts) { v.Joy.Pawns[0].Bored = nil },
		func(v *o.ComfortFacts) { v.Joy.Pawns[0].Tolerance[0] = math.NaN() },
		func(v *o.ComfortFacts) { v.Joy.Pawns[0].Tolerance[0] = -0.1 },
		func(v *o.ComfortFacts) { v.Joy.Pawns[0].Pawn = "outsider" },
		func(v *o.ComfortFacts) { v.Joy.Pawns = append(v.Joy.Pawns, v.Joy.Pawns[0]) },
		func(v *o.ComfortFacts) { v.Joy.Pawns[0] = nil },
		func(v *o.ComfortFacts) { v.Joy.Methods[0].PowerW = math.Inf(1) },
		func(v *o.ComfortFacts) { v.Joy.Methods[0].Definition = "invented" },
		func(v *o.ComfortFacts) { v.Joy.Kinds = []string{"missing"} },
		func(v *o.ComfortFacts) { v.Joy.Pawns = make([]*o.JoyTolerance, 257) },
		func(v *o.ComfortFacts) { v.Joy.Kinds = make([]string, 17) },
		func(v *o.ComfortFacts) { v.Joy.Kinds = make([]string, 16); v.Joy.Pawns = make([]*o.JoyTolerance, 129) },
	} {
		v := valid()
		mutate(v)
		if validateRecreationCensus(v, map[string]bool{"p": true}) == nil {
			t.Fatal("invalid recreation census accepted", v)
		}
	}
}

func comfortWire() *o.UpkeepFacts {
	complete := func(n uint64) *o.Completeness {
		return &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(n), Returned: proto.Uint64(n), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}
	}
	return &o.UpkeepFacts{Completeness: complete(1), Comfort: &o.ComfortSection{Outcome: &o.ComfortSection_Observed{Observed: &o.ComfortFacts{
		Completeness: complete(1), People: []string{"p"}, Surfaces: []*o.ComfortSurface{{Id: proto.String("table"), Adjacent: []*c.Cell{{X: proto.Int32(1), Z: proto.Int32(2)}}}},
		Dining:     []*o.ComfortFacility{{Id: proto.String("chair"), AccessibleTo: []string{"p"}, Users: []string{"p"}}},
		Recreation: []*o.ComfortFacility{{Id: proto.String("hoop"), Kind: proto.String("Dexterity"), AccessibleTo: []string{"p"}}},
	}}}}
}

func TestColonyComfortStrictCensusAndAdjacency(t *testing.T) {
	size := &o.MapSize{Width: proto.Uint32(50), Height: proto.Uint32(50)}
	if err := validateColonyUpkeep(comfortWire(), size); err != nil {
		t.Fatal(err)
	}
	for _, mutate := range []func(*o.UpkeepFacts){
		func(v *o.UpkeepFacts) { v.Comfort.GetObserved().Completeness.Page.Complete = proto.Bool(false) },
		func(v *o.UpkeepFacts) { v.Comfort.GetObserved().People = []string{"p", "p"} },
		func(v *o.UpkeepFacts) { v.Comfort.GetObserved().Dining[0].Users = []string{"outsider"} },
		func(v *o.UpkeepFacts) { v.Comfort.GetObserved().Dining[0].AccessibleTo = []string{"p", "p"} },
		func(v *o.UpkeepFacts) { v.Comfort.GetObserved().Recreation[0].Kind = nil },
		func(v *o.UpkeepFacts) { v.Comfort.GetObserved().Surfaces[0].Adjacent[0].X = proto.Int32(50) },
		func(v *o.UpkeepFacts) {
			v.Comfort.GetObserved().Surfaces = append(v.Comfort.GetObserved().Surfaces, v.Comfort.GetObserved().Surfaces[0])
		},
	} {
		v := comfortWire()
		mutate(v)
		if err := validateColonyUpkeep(v, size); err == nil {
			t.Fatal("invalid comfort projection accepted", v)
		}
	}
	v := comfortWire()
	v.Comfort = &o.ComfortSection{Outcome: &o.ComfortSection_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_UNSUPPORTED.Enum(), Detail: proto.String("unavailable")}}}
	if err := validateColonyUpkeep(v, size); err != nil {
		t.Fatal("explicit unavailable comfort rejected", err)
	}
	// The lighting section (issue #6 slice 3) rides the same projection.
	v = comfortWire()
	v.Lighting = lightingWire()
	if err := validateColonyUpkeep(v, size); err != nil {
		t.Fatal("lighting section rejected by the comfort projection", err)
	}
}
