package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func tender(id string, current bool, quality float64) SelfTendPawn {
	p := SelfTendPawn{ID: PawnID(id), Current: domain.Known(current), Available: domain.Known(true), Read: true}
	if quality >= 0 {
		p.TendQuality = domain.Known(quality)
	}
	return p
}

func TestWantSelfTend(t *testing.T) {
	downed := tender("Doc", false, 1.2)
	downed.Available = domain.Known(false)
	guest := tender("Guest", false, 1.5)
	guest.Guest = true
	unread := SelfTendPawn{ID: "Other", Current: domain.Known(false), Available: domain.Known(true)}
	for _, tc := range []struct {
		name        string
		pawns       []SelfTendPawn
		want, known bool
	}{
		{"alone", []SelfTendPawn{tender("A", false, 0.5)}, true, true},
		{"beats doctor", []SelfTendPawn{tender("A", false, 1.0), tender("Doc", false, 0.7)}, true, true},
		{"loses to doctor", []SelfTendPawn{tender("A", true, 1.0), tender("Doc", false, 0.71)}, false, true},
		{"other cannot doctor", []SelfTendPawn{tender("A", false, 0.3), tender("B", false, -1)}, true, true},
		{"doctor downed", []SelfTendPawn{tender("A", false, 0.3), downed}, true, true},
		{"guest ignored", []SelfTendPawn{tender("A", false, 0.3), guest}, true, true},
		{"other unread", []SelfTendPawn{tender("A", false, 0.3), unread}, false, false},
		{"self cannot doctor", []SelfTendPawn{tender("A", false, -1)}, false, false},
	} {
		got, known := WantSelfTend(tc.pawns, 0)
		if got != tc.want || known != tc.known {
			t.Errorf("%s: got %v/%v want %v/%v", tc.name, got, known, tc.want, tc.known)
		}
	}
}

func TestSelfTendChanges(t *testing.T) {
	pawns := []SelfTendPawn{tender("A", true, 0.5), tender("Doc", false, 1.0)}
	changes := SelfTendChanges(pawns)
	// A loses to Doc (0.35 < 1.0): off. Doc beats A (0.7 >= 0.5): on.
	if len(changes) != 2 || changes[0].Pawn() != "A" || changes[0].SelfTend() || changes[1].Pawn() != "Doc" || !changes[1].SelfTend() {
		t.Fatalf("changes = %+v", changes)
	}
	if owed, _ := SelfTendOwed(domain.Known(pawns)).Value(); !owed {
		t.Fatal("owed")
	}
}
