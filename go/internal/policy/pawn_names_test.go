package policy

import (
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestNicknameChangesRenamesTheNewerBob(t *testing.T) {
	got := NicknameChanges([]OwnedName{
		{Pawn: "Thing_Human30", Short: "Bob", ThingID: 30},
		{Pawn: "Thing_Human12", Short: "Bob", ThingID: 12},
		{Pawn: "Thing_Muffalo40", Short: "bob", ThingID: 40},
		{Pawn: "Thing_Human50", Short: "Ann", ThingID: 50},
	})
	if len(got) != 2 || got[0].Pawn() != "Thing_Human30" || got[1].Pawn() != "Thing_Muffalo40" {
		t.Fatalf("renames = %+v, want the two newer Bobs", got)
	}
	for _, s := range got {
		if s.LeaveName() == "" || strings.ContainsAny(s.LeaveName(), "0123456789") {
			t.Fatalf("rename %+v must leave the colliding name, never pick a numbered one", s)
		}
	}
	if owed, _ := NamesOwed(domain.Known([]OwnedName{{Pawn: "a", Short: "Bob", ThingID: 1}})).Value(); owed {
		t.Fatal("one Bob owes nothing")
	}
	if _, known := NamesOwed(domain.Unknown[[]OwnedName]()).Value(); known {
		t.Fatal("unknown census must stay unknown")
	}
}
