package bridge

import (
	"context"
	"encoding/json"
	"errors"
	"testing"

	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"

	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

func consumptionHour(hour int32, rows ...*o.ConsumptionRow) *o.ConsumptionHour {
	return &o.ConsumptionHour{Hour: proto.Int32(hour), Rows: rows}
}

func consumptionRow(def, reason string, count int64) *o.ConsumptionRow {
	return &o.ConsumptionRow{Definition: proto.String(def), Reason: proto.String(reason), Count: proto.Int64(count)}
}

// TestReadConsumption covers the realized-consumption read against a
// fake native: the since hour rides the request (absent for the whole window),
// the hours decode sparse, and a malformed page is a contract error.
func TestReadConsumption(t *testing.T) {
	var got *o.ConsumptionRequest
	snapshot := &o.ConsumptionSnapshot{CurrentHour: proto.Int32(60), FirstHour: proto.Int32(10), Hours: []*o.ConsumptionHour{
		consumptionHour(12, consumptionRow("Steel", "bill_ingredient", 30), consumptionRow("Steel", "fuel_loaded", -2)),
		consumptionHour(50, consumptionRow("MedicineHerbal", "medicine_tend", 4)),
	}}
	s := &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*callResult, error) {
		var envelope struct {
			Request string `json:"request"`
		}
		if err := json.Unmarshal(arg.Arguments, &envelope); err != nil {
			t.Fatal(err)
		}
		got = &o.ConsumptionRequest{}
		if err := protojson.Unmarshal([]byte(envelope.Request), got); err != nil {
			t.Fatal(err)
		}
		return pbResult(&o.ConsumptionReply{Outcome: &o.ConsumptionReply_Observed{Observed: snapshot}}), nil
	}}
	client := testClient(t, s, testBudget)
	ctx := context.Background()

	page, err := client.ReadConsumption(ctx, -1)
	if err != nil || got.SinceHour != nil {
		t.Fatalf("whole window: %v %v", err, got)
	}
	if page.CurrentHour != 60 || page.FirstHour != 10 || len(page.Hours) != 2 || page.Hours[0].Rows[1].Count != -2 || page.Hours[1].Rows[0].Reason != "medicine_tend" {
		t.Fatalf("page: %+v", page)
	}
	snapshot.Hours = snapshot.Hours[1:]
	if page, err = client.ReadConsumption(ctx, 49); err != nil || got.GetSinceHour() != 49 || len(page.Hours) != 1 {
		t.Fatalf("since: %v %+v %v", err, page, got)
	}
	for name, bad := range map[string]*o.ConsumptionSnapshot{
		"hour before since":  {CurrentHour: proto.Int32(60), FirstHour: proto.Int32(10), Hours: []*o.ConsumptionHour{consumptionHour(49)}},
		"unfinished hour":    {CurrentHour: proto.Int32(60), FirstHour: proto.Int32(10), Hours: []*o.ConsumptionHour{consumptionHour(60)}},
		"unknown reason":     {CurrentHour: proto.Int32(60), FirstHour: proto.Int32(10), Hours: []*o.ConsumptionHour{consumptionHour(55, consumptionRow("Steel", "nonsense", 1))}},
		"missing count":      {CurrentHour: proto.Int32(60), FirstHour: proto.Int32(10), Hours: []*o.ConsumptionHour{consumptionHour(55, &o.ConsumptionRow{Definition: proto.String("Steel"), Reason: proto.String("rot")})}},
		"missing hours info": {},
	} {
		snapshot = bad
		if _, err := client.ReadConsumption(ctx, 49); !errors.Is(err, ErrContract) {
			t.Fatalf("%s: %v", name, err)
		}
	}
}
