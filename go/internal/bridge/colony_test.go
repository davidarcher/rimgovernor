package bridge

import (
	"context"
	"encoding/json"
	"os"
	"testing"
	"time"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"github.com/modelcontextprotocol/go-sdk/mcp"
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
	names := []string{"Wall"}
	client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
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
		if !q.GetPlanning() || q.Page.GetLimit() != 256 || len(q.RequestedDefinitionNames) != 1 || q.RequestedDefinitionNames[0] != "Wall" {
			t.Fatal(q)
		}
		id.LoadToken = proto.String("changed")
		names[0] = "changed"
		return pbResult(r), nil
	}}, time.Second)
	reply, _, err := client.ReadColonyFacts(context.Background(), id, true, names)
	if err != nil || reply.GetObserved().FoodNutrition != nil {
		t.Fatal(reply, err)
	}
}
func TestColonyRefusesMalformedAndIncompleteNativeFacts(t *testing.T) {
	for _, change := range []string{"world", "stock", "duplicate", "partial", "geometry", "cell-duplicate", "issue", "unavailable", "numbers"} {
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
			case "partial":
				r.Completeness.Page.Complete = proto.Bool(false)
			case "geometry":
				r.Planning.GetObserved().Cells.Cells[0].Cell.X = proto.Int32(99)
			case "cell-duplicate":
				p := r.Planning.GetObserved().Cells
				p.Cells = append(p.Cells, p.Cells[0])
			case "issue":
				r.Issues = []*o.ReadIssue{{Field: proto.String("bed_capacity"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_READ_FAILED.Enum()}}}
			case "unavailable":
				r.FoodSupply = nil
			case "numbers":
				r.WorkerCount = proto.Uint32(100)
			}
			if err := ValidateColonyFacts(r, id); err == nil {
				t.Fatal("malformed facts accepted")
			}
		})
	}
}
