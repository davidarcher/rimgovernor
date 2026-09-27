package bridge

import (
	"context"
	"strings"
	"testing"

	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
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
	request := &o.ExecuteRequest{Precondition: buildingPre(), Operation: &o.Operation{}}
	for _, want := range []string{"Clearance: deconstruct", ""} {
		got := stampOperationIntent(WithOperationIntent(context.Background(), want), "rimgovernor/operations_execute", request).(*o.ExecuteRequest)
		if got.GetOperation().GetIntent() != want || (want == "") != (got == request) || request.GetOperation().Intent != nil {
			t.Fatal(want, got)
		}
	}
}
