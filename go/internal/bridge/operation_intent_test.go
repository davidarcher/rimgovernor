package bridge

import (
	"context"
	"strings"
	"testing"
	"time"

	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

func TestFormatOperationIntent(t *testing.T) {
	for _, c := range []struct{ label, reason, want string }{
		{"Hunting", "food runway 3d", "Hunting: food runway 3d"},
		{"Bedroom", "", "Bedroom"},
		{"", "cut", "cut"},
		{"", "", ""},
		{"Build", "bedroom\tfor  Kíra\n", "Build: bedroom for Kra"},
	} {
		if got := FormatOperationIntent(c.label, c.reason); got != c.want {
			t.Errorf("%q %q = %q, want %q", c.label, c.reason, got, c.want)
		}
	}
	if got := FormatOperationIntent("Wood", strings.Repeat("x", 200)); len(got) != MaxOperationIntent {
		t.Fatal(len(got))
	}
}

func TestExecuteCarriesContextIntent(t *testing.T) {
	var want string
	client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*callResult, error) {
		expected := deconstructionOperation("ruin")
		if want != "" {
			expected.Intent = proto.String(want)
		}
		draftTestRequest(t, arg, &o.ExecuteRequest{Precondition: buildingPre(), Operation: expected})
		return pbResult(&o.ExecuteReply{Outcome: &o.ExecuteReply_Receipt{Receipt: deconstructionTestReceipt()}}), nil
	}}, time.Second)
	writer, _ := NewDeconstructionWriter(client)
	for _, want = range []string{"Clearance: deconstruct", ""} {
		if _, _, err := writer.ApplyDeconstruction(WithOperationIntent(context.Background(), want), buildingPre(), "ruin"); err != nil {
			t.Fatal(want, err)
		}
	}
}
