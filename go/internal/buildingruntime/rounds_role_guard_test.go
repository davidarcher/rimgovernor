package buildingruntime

import (
	"bytes"
	"context"
	"log/slog"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func roleGuardFacts(t *testing.T, standing bool, native policy.RoomRole, medicalBed bool) observation.ColonyProjection {
	t.Helper()
	score := func(role policy.RoomRole, when policy.RoleCondition) policy.RoleContribution {
		s, ok := policy.RoomRoleScore(role, when)
		if !ok {
			t.Fatal("no score for", role)
		}
		return s
	}
	catalog := policy.StationCatalog{Stations: map[string]policy.Station{
		"Stove": {Contributions: []policy.RoleContribution{score(policy.RoomRoleKitchen, policy.CondAlways)}},
		"Bed": {EmptyCountsForBarracks: true, Contributions: []policy.RoleContribution{
			score(policy.RoomRoleBedroom, policy.CondBedFree), score(policy.RoomRoleBarracks, policy.CondBedFree),
			score(policy.RoomRoleHospital, policy.CondBedMedical),
			score(policy.RoomRolePrisonCell, policy.CondBedPrisoner), score(policy.RoomRolePrisonCell, policy.CondBedPrisonerMedical),
			score(policy.RoomRolePrisonBarracks, policy.CondBedPrisoner), score(policy.RoomRolePrisonBarracks, policy.CondBedPrisonerMedical),
		}},
	}}
	region := policy.RectRegion(policy.Rectangle{X: 10, Z: 10, Width: 4, Height: 4})
	plan := policy.LayoutPlan{Rooms: []policy.PlannedRoom{{ID: 7, Role: policy.PlannedKitchen, Region: region}}}
	building := func(id, def string, cell domain.Cell) policy.CurrentBuilding {
		b, err := domain.NewBuilding(def, cell, domain.North, "")
		if err != nil {
			t.Fatal(err)
		}
		return policy.CurrentBuilding{ID: id, Building: b, Cells: []domain.Cell{cell}}
	}
	construction := policy.CurrentConstruction{Colony: true, Buildings: []policy.CurrentBuilding{
		building("s", "Stove", domain.Cell{X: 11, Z: 11}),
		building("b", "Bed", domain.Cell{X: 12, Z: 12}),
		building("outside", "Bed", domain.Cell{X: 30, Z: 30}),
	}}
	var rooms policy.RoomObservation
	if standing {
		rooms.Rooms = []policy.Room{{ID: "r", Role: domain.Known(native), Enclosed: domain.Known(true), Cells: region.Cells()}}
	}
	sleeping := policy.SleepingObservation{Beds: []policy.SleepingBed{
		{ID: "b", Medical: domain.Known(medicalBed), Prisoners: domain.Known(false)},
		{ID: "outside", Medical: domain.Known(true), Prisoners: domain.Known(false)},
	}}
	var facts observation.ColonyProjection
	facts.LayoutPlan, facts.Rooms, facts.Stations = domain.Known(plan), domain.Known(rooms), catalog
	facts.Facts.CurrentConstruction, facts.Facts.Sleeping = domain.Known(construction), domain.Known(sleeping)
	return facts
}

func TestRoleGuardRecordsAKitchenHoldingAMedicalBed(t *testing.T) {
	facts := roleGuardFacts(t, true, policy.RoomRoleKitchen, true)
	got, err := roleMismatches(facts)
	if err != nil {
		t.Fatal(err)
	}
	if len(got) != 1 || got[0].Room != 7 || got[0].Native != policy.RoomRoleKitchen || got[0].Predicted != policy.RoomRoleHospital || got[0].Scores[policy.RoomRoleHospital] == 0 {
		t.Fatalf("mismatches = %+v, want room 7 kitchen predicted hospital with scores", got)
	}
	var logs bytes.Buffer
	old := slog.Default()
	slog.SetDefault(slog.New(slog.NewTextHandler(&logs, nil)))
	defer slog.SetDefault(old)
	recordRoleMismatches(context.Background(), facts)
	if !strings.Contains(logs.String(), policy.RoleMismatchReason) || strings.Count(logs.String(), "layout_role_guard") < 1 {
		t.Fatalf("no mismatch decision logged: %s", logs.String())
	}
}

func TestRoleGuardRecordsNothingWhenRolesAgreeOrRoomIsNotStanding(t *testing.T) {
	cases := map[string]observation.ColonyProjection{
		"roles agree":  roleGuardFacts(t, true, policy.RoomRoleHospital, true),
		"free bed":     roleGuardFacts(t, true, policy.RoomRoleBedroom, false),
		"not standing": roleGuardFacts(t, false, policy.RoomRoleKitchen, true),
	}
	for name, facts := range cases {
		got, err := roleMismatches(facts)
		if err != nil || len(got) != 0 {
			t.Fatalf("%s: mismatches = %+v, err = %v", name, got, err)
		}
	}
}
