package observation

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
	"os"
	"path/filepath"
	"testing"
)

func TestColonyProjectionKeepsRawFoodAndUnknownGeometryOutOfPolicy(t *testing.T) {
	data, err := os.ReadFile("../../../contracts/fixtures/colony-core.json")
	if err != nil {
		t.Fatal(err)
	}
	r := &o.ColonyFactsReply{}
	if err = protojson.Unmarshal(data, r); err != nil {
		t.Fatal(err)
	}
	expected := Identity{Colony: "colony", Load: "load", Map: 0, Tick: 7, NativeGeneration: domain.Known(domain.NativeGeneration(1))}
	p, err := DecodeColony(r, expected)
	if err != nil {
		t.Fatal(err)
	}
	if _, known := p.Facts.FoodDays.Value(); known {
		t.Fatal("raw runway became forecast")
	}
	if wood, known := p.Facts.Wood.Value(); !known || wood != 40 {
		t.Fatal(p)
	}
	if roof, known := p.Cells[0].Roofed.Value(); !known || roof {
		t.Fatal("native no-roof not preserved")
	}
	if occupied, known := p.Cells[0].Occupied.Value(); !known || occupied {
		t.Fatal("known free cell lost")
	}
	r.GetObserved().Planning.GetObserved().Cells.Cells[0].Occupied = nil
	p, err = DecodeColony(r, expected)
	if err != nil {
		t.Fatal(err)
	}
	if _, known := p.Cells[0].Occupied.Value(); known {
		t.Fatal("missing occupancy became empty")
	}
	r.GetObserved().Resources[0].Units = nil
	p, err = DecodeColony(r, expected)
	if err != nil {
		t.Fatal(err)
	}
	if _, known := p.Facts.Wood.Value(); known {
		t.Fatal("missing stock count became zero")
	}
	r.GetObserved().Context.Tick = proto.Int64(8)
	r.GetObserved().Planning.GetObserved().Cells.Context.Tick = proto.Int64(8)
	if _, err = DecodeColony(r, expected); err == nil {
		t.Fatal("mixed review tick accepted")
	}
}

// Set this to a retained official ProtoJSON payload from native acceptance.
// This proves the actual C# projection reaches durable Go review unchanged.
func TestColonyNativeCaptureReachesRoutineReview(t *testing.T) {
	path := os.Getenv("RIMGOVERNOR_NATIVE_COLONY_CAPTURE")
	if path == "" {
		t.Skip("requires retained native colony acceptance payload")
	}
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	r := &o.ColonyFactsReply{}
	if err = protojson.Unmarshal(data, r); err != nil {
		t.Fatal(err)
	}
	identity, err := contextIdentity(r.GetObserved().Context)
	if err != nil {
		t.Fatal(err)
	}
	p, err := DecodeColony(r, identity)
	if err != nil {
		t.Fatal(err)
	}
	if len(p.Cells) == 0 || len(p.Definitions) == 0 {
		t.Fatal("missing native planning data")
	}
	s, err := store.Open(context.Background(), filepath.Join(t.TempDir(), "native-review.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer s.Close()
	current := domain.GenerationSnapshot{Colony: identity.Colony, Load: identity.Load, Map: identity.Map, Direction: 1, Plan: "native-review", Revision: 1, Native: 1}
	out, err := s.ReviewRoutine(context.Background(), store.RoutineReviewRequest{Current: current, Tick: identity.Tick, Enabled: true, Policy: policy.DefaultRoutinePolicy(), Facts: p.Facts})
	if err != nil {
		t.Fatal(err)
	}
	if len(out.Goals) != 14 {
		t.Fatal("native facts did not reach maintained goals")
	}
	for _, assessment := range out.Needs.Assessments {
		if assessment.ID == policy.EnsureFoodSupply && assessment.Need != domain.NeedUnknown {
			t.Fatal("raw native runway certified food need", assessment)
		}
	}
	t.Logf("Native core and %d cells/%d definitions reached durable routine review", len(p.Cells), len(p.Definitions))
}
