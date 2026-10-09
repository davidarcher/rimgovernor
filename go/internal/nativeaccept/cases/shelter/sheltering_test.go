package shelter

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// An empty journal (a restarted service before its first review) must not
// read as restored: shelter/toxic_fallout passed its restore wait on map[]
// and then found the pawns still in the Safe area.
func TestAreasSettledNeedsEvidence(t *testing.T) {
	pawns := []string{"a", "b"}
	none := map[string]domain.Tick{}
	if areasSettled(map[string]string{}, none, 0, false, pawns, false) {
		t.Fatal("empty journal settled the restore")
	}
	if areasSettled(map[string]string{}, none, 0, false, pawns, true) {
		t.Fatal("empty journal settled the shelter")
	}
	sheltered := map[string]string{"a": "Safe", "b": "Safe"}
	at := map[string]domain.Tick{"a": 10, "b": 12}
	if !areasSettled(sheltered, at, 0, false, pawns, true) {
		t.Fatal("sheltered pawns did not settle the shelter")
	}
	if areasSettled(sheltered, at, 0, false, pawns, false) {
		t.Fatal("sheltered pawns settled the restore")
	}
	if areasSettled(sheltered, at, 12, true, pawns, false) {
		t.Fatal("a Safe area delete at b's move tick restored b")
	}
	if !areasSettled(sheltered, at, 20, true, pawns, false) {
		t.Fatal("a later Safe area delete did not restore")
	}
	if !areasSettled(map[string]string{"a": "", "b": ""}, at, 0, false, pawns, false) {
		t.Fatal("cleared pawns did not settle the restore")
	}
}
