package bridge

import (
	"context"
	"strings"
	"testing"

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

func TestApplyStampsContextPurpose(t *testing.T) {
	own := &o.Action{Key: proto.String("b"), Purpose: proto.String("own")}
	plain := &o.Action{Key: proto.String("a")}
	actions := []*o.Action{plain, own}
	if got := stampPurpose(context.Background(), actions); &got[0] != &actions[0] {
		t.Fatal("no intent must not copy")
	}
	got := stampPurpose(WithOperationIntent(context.Background(), "Clearance: deconstruct"), actions)
	if got[0].GetPurpose() != "Clearance: deconstruct" || got[1] != own || plain.Purpose != nil {
		t.Fatal(got)
	}
}
