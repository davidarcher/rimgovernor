package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestGuardNativeRoles(t *testing.T) {
	cat := roleTestCatalog()
	region := RectRegion(Rectangle{X: 10, Z: 10, Width: 4, Height: 4})
	plan := LayoutPlan{Rooms: []PlannedRoom{{ID: 7, Role: PlannedKitchen, Region: region}}}
	census := func(role domain.Fact[RoomRole]) RoomObservation {
		return RoomObservation{Rooms: []Room{{ID: "r", Role: role, Enclosed: domain.Known(true), Cells: region.Cells()}}}
	}
	kitchen := []RoomPiece{piece("Stove", ""), piece("Bench", "")}
	hospitalInKitchen := append(append([]RoomPiece(nil), kitchen...), piece("Bed", CondBedMedical))
	cases := []struct {
		name     string
		contents []RoomContents
		rooms    RoomObservation
		want     []RoleMismatch
	}{
		{"match", []RoomContents{{Room: 7, Pieces: kitchen}}, census(domain.Known(RoomRoleKitchen)), nil},
		{"native differs", []RoomContents{{Room: 7, Pieces: hospitalInKitchen}}, census(domain.Known(RoomRoleKitchen)),
			[]RoleMismatch{{Room: 7, Native: RoomRoleKitchen, Predicted: RoomRoleHospital}}},
		{"unknown native role skipped", []RoomContents{{Room: 7, Pieces: hospitalInKitchen}}, census(domain.Unknown[RoomRole]()), nil},
		{"not standing skipped", []RoomContents{{Room: 7, Pieces: hospitalInKitchen}}, RoomObservation{}, nil},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			got, err := GuardNativeRoles(cat, plan, c.rooms, c.contents)
			if err != nil {
				t.Fatal(err)
			}
			if len(got) != len(c.want) {
				t.Fatalf("got %+v, want %+v", got, c.want)
			}
			for i, w := range c.want {
				if got[i].Room != w.Room || got[i].Native != w.Native || got[i].Predicted != w.Predicted {
					t.Fatalf("got %+v, want %+v", got[i], w)
				}
			}
		})
	}
	if _, err := GuardNativeRoles(cat, plan, RoomObservation{}, []RoomContents{{Room: 9}}); err == nil {
		t.Fatal("contents of a room outside the plan are an error")
	}
	if _, err := GuardNativeRoles(cat, plan, census(domain.Known(RoomRoleKitchen)), []RoomContents{{Room: 7, Pieces: []RoomPiece{piece("Nope", "")}}}); err == nil {
		t.Fatal("a piece outside the catalog is an error")
	}
}
