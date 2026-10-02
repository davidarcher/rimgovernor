package bridge

import (
	"context"
	"encoding/json"
	"math"
	"os"
	"testing"
	"time"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

func colonyFixture(t *testing.T) *o.ColonyFactsReply {
	t.Helper()
	data, err := os.ReadFile("../../../contracts/fixtures/colony-core.json")
	if err != nil {
		t.Fatal(err)
	}
	r := &o.ColonyFactsReply{}
	if err = protojson.Unmarshal(data, r); err != nil {
		t.Fatal(err)
	}
	return r
}
func TestColonyFixedReadOwnsSelectionAndPreservesOptionalFacts(t *testing.T) {
	r := colonyFixture(t)
	id := proto.Clone(r.GetObserved().Context.Identity).(*c.Identity)
	client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*callResult, error) {
		if arg.Tool != "rimgovernor/observations_read_colony_facts" {
			t.Fatal(arg.Tool)
		}
		var outer struct {
			Request string `json:"request"`
		}
		if err := json.Unmarshal(arg.Arguments, &outer); err != nil {
			t.Fatal(err)
		}
		q := &o.ColonyFactsRequest{}
		if err := protojson.Unmarshal([]byte(outer.Request), q); err != nil {
			t.Fatal(err)
		}
		if !q.GetPlanning() {
			t.Fatal(q)
		}
		id.LoadToken = proto.String("changed")
		return pbResult(r), nil
	}}, time.Second)
	reply, _, err := client.ReadColonyFacts(context.Background(), id, true)
	if err != nil || reply.GetObserved().FoodNutrition != nil {
		t.Fatal(reply, err)
	}
}
func TestColonyRefusesMalformedAndIncompleteNativeFacts(t *testing.T) {
	for _, change := range []string{"world", "stock", "duplicate", "issue", "unavailable", "numbers", "environment-cell", "environment-room", "crop-demand", "crop-diet", "crop-duplicate", "calendar-day", "calendar-season", "calendar-until"} {
		t.Run(change, func(t *testing.T) {
			r := colonyFixture(t).GetObserved()
			id := proto.Clone(r.Context.Identity).(*c.Identity)
			switch change {
			case "world":
				r.Context.Identity.LoadToken = proto.String("wrong")
			case "stock":
				r.Resources[0].Units = proto.Int64(-1)
			case "duplicate":
				r.Resources = append(r.Resources, r.Resources[0])
			case "issue":
				r.Issues = []*o.ReadIssue{{Field: proto.String("bed_capacity"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_READ_FAILED.Enum()}}}
			case "unavailable":
				r.FoodSupply = nil
			case "numbers":
				r.WorkerCount = proto.Uint32(100)
			case "environment-cell":
				r.Planning.GetObserved().Environment = &o.ControlledEnvironment{Lights: []*o.GrowLight{{Building: &o.EntityRef{Id: proto.String("lamp"), DefName: proto.String("SunLamp"), Position: &c.Cell{X: proto.Int32(1), Z: proto.Int32(1)}}, GrowthCells: []*c.Cell{{X: proto.Int32(-1), Z: proto.Int32(0)}}}}}
			case "crop-demand":
				r.Planning.GetObserved().Crops[0].NutritionDemandPerDay = proto.Float64(math.NaN())
			case "crop-diet":
				r.Planning.GetObserved().Crops[0].DietAllowed = nil
			case "crop-duplicate":
				r.Planning.GetObserved().Crops = append(r.Planning.GetObserved().Crops, r.Planning.GetObserved().Crops[0])
			case "calendar-day":
				r.FoodClimate = &o.FoodClimate{GrowingDays: proto.Float64(40), GrowingDaysRemaining: proto.Float64(10), GrowingDaysUntil: proto.Float64(0), SowingNow: proto.Bool(true), DayOfYear: proto.Int32(60), Season: proto.String("Fall")}
			case "calendar-season":
				r.FoodClimate = &o.FoodClimate{GrowingDays: proto.Float64(40), GrowingDaysRemaining: proto.Float64(10), GrowingDaysUntil: proto.Float64(0), SowingNow: proto.Bool(true), DayOfYear: proto.Int32(40), Season: proto.String("Autumn")}
			case "calendar-until":
				r.FoodClimate = &o.FoodClimate{GrowingDays: proto.Float64(40), GrowingDaysRemaining: proto.Float64(10), GrowingDaysUntil: proto.Float64(61), SowingNow: proto.Bool(true)}
			case "calendar-non-growing":
				r.FoodClimate = &o.FoodClimate{GrowingDays: proto.Float64(40), GrowingDaysRemaining: proto.Float64(10), GrowingDaysUntil: proto.Float64(0), NonGrowingDays: proto.Float64(-1), SowingNow: proto.Bool(true)}
			case "environment-room":
				r.Planning.GetObserved().Environment = &o.ControlledEnvironment{Rooms: []*o.GrowRoom{{Room: &c.Ref{Id: proto.String("7")}, CellCount: proto.Uint32(4), LitCells: proto.Uint32(5)}}}
			}
			if err := ValidateColonyFacts(r, id); err == nil {
				t.Fatal("malformed facts accepted")
			}
		})
	}
}
