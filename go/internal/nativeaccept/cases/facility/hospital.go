package facility

import (
	"context"
	"fmt"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/sustained"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/sustainedfood"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// The hospital case (issue #4 M3): two seeded flu patients who may seek
// bed rest (test/medical_management_setup with hospital=true: no pre-placed
// medical spots), the tend, rescue, medicine-reserve and hospital families
// composed over the startup ladder, and MaintainMedicalCare watched until
// the hospital planner's bed_medical patch completes. A humanlike bed must
// read medical inside a room whose native Room.Role hosts the Hospital
// facility, and one of the seeded patients must be lying in that bed within
// the settle window after the service stops.

// hospitalFamilies composes the startup ladder plus the families a
// MaintainMedicalCare deficit walks through: tend and rescue own the
// patients, medical the medicine reserve, hospital the hosted bed, work the
// Doctor coverage the reserve and tending need.
const hospitalFamilies = "sleeping,shelter,temperature,comfort,work,supply,tend,rescue,medical,hospital"

// settle is the wall-clock window after the service stops for a patient to
// reach the hospital bed under the game's own AI.
const settle = 4 * time.Minute

// shelterStage is the hospital case's one stage (#329): the startup
// ladder's initial shelter recovered on the baseline, before the sleeping
// family furnishes it and the hospital planner has a bed to convert. The
// bulk of the window is the shell; the conversion after it runs on a miss
// and a hit alike.
const shelterStage = "shelter-served"

func init() {
	cases.Register(cases.Case{
		Name:  "facility/hospital",
		Scope: "MaintainMedicalCare's patients get a medical bed inside a room whose native role hosts the Hospital facility, and a seeded flu patient lies in it; the bed's medical flag and the patient's bed are read live, never from receipts (issue #4, M3).",
		Start: cases.Fixture{
			Op:   "test/medical_management_setup",
			Args: map[string]any{"disease": true, "failSurgery": false, "manualTending": false, "withdrawal": false, "hospital": true},
			On:   cases.Save{Name: sustained.BaselineSave},
		},
		// Patients rest in the bed the planner provides: Rest stays live.
		Keep:   []string{string(na.NeedRest)},
		Serve:  spec("hospital", hospitalFamilies),
		Stages: []string{shelterStage},
		Budget: 2*window + 5*time.Minute,
		Reason: "the startup ladder's shell is a cached stage (#329); the bed and its conversion after it run on a miss and a hit alike",
		Run: func(ctx context.Context, s cases.Session) error {
			prepared := s.Prepared()
			var patients []string
			for _, raw := range na.AsSlice(prepared["patients"]) {
				patients = append(patients, na.AsString(raw))
			}
			if len(patients) != 2 {
				return fmt.Errorf("medical_management_setup seeded %d patients, want 2", len(patients))
			}
			if err := s.Stage(ctx, shelterStage, func(ctx context.Context) error {
				_, err := sustainedfood.Observe(ctx, s, sustainedfood.Observation{
					WatchConfig: sustainedfood.WatchConfig{Watch: window, Goal: policy.MaintainMedicalCare, Extra: []policy.GoalID{policy.EnsureInitialShelter}, Until: shelterRecovered},
					Prepare: func(ctx context.Context, h *na.Harness, report na.Report) error {
						hosted, err := hospitalBeds(ctx, h, "baseline")
						if err != nil {
							return err
						}
						report["baseline_hospital_beds"] = hosted
						if len(hosted) > 0 {
							return fmt.Errorf("save already holds a hosted medical bed; nothing for the hospital planner to provide")
						}
						return nil
					},
					Audit: func(ctx context.Context, h *na.Harness, report na.Report) error {
						if !shelterRecovered(lastSample(report)) {
							return fmt.Errorf("EnsureInitialShelter did not recover within the watch window")
						}
						// The stage must end before the conversion the tail
						// proves, or a hit has nothing left to watch.
						hosted, err := hospitalBeds(ctx, h, "shelter-stage")
						if err != nil {
							return err
						}
						report["shelter_stage_hospital_beds"] = hosted
						if len(hosted) > 0 {
							return fmt.Errorf("a hosted medical bed already stands at the shelter stage; the conversion ran before the bundle")
						}
						return nil
					},
				})
				return err
			}); err != nil {
				return err
			}
			_, err := sustainedfood.Observe(ctx, s, sustainedfood.Observation{
				WatchConfig: sustainedfood.WatchConfig{Watch: window, Goal: policy.MaintainMedicalCare, Until: bedConverted},
				Audit: func(ctx context.Context, h *na.Harness, report na.Report) error {
					return auditHospital(ctx, h, report, patients, settle, 5*time.Second)
				},
			})
			return err
		},
	})
}

// shelterRecovered reports a sample whose EnsureInitialShelter reading
// (an Extra goal, under its id) is recovered and satisfied: the startup
// shell stands.
func shelterRecovered(sample map[string]any) bool {
	shelter, _ := sample[string(policy.EnsureInitialShelter)].(map[string]any)
	return comfortRecovered(shelter)
}

// lastSample is the newest timeline row the watch left on the report.
func lastSample(report na.Report) map[string]any {
	timeline, _ := report["timeline"].([]map[string]any)
	if len(timeline) == 0 {
		return nil
	}
	return timeline[len(timeline)-1]
}

// bedConverted reports a sample whose MaintainMedicalCare goal holds or held
// (retired_plans: a completed method leaves the goal at the next review) a
// hospital plan made only of bed_medical patches with every action
// completed: the bed reads medical natively. The ladder's shell and bed
// plans share the routine-hospital prefix and never count.
func bedConverted(sample map[string]any) bool {
	plans, _ := sample["plans"].([]map[string]any)
	retired, _ := sample["retired_plans"].([]map[string]any)
	for _, plan := range append(plans, retired...) {
		id, _ := plan["plan"].(string)
		actions, _ := plan["actions"].(int)
		stages, _ := plan["stages"].(map[string]int)
		kinds, _ := plan["kinds"].(map[string]int)
		if strings.HasPrefix(id, "routine-hospital-") && actions > 0 && stages["completed"] == actions && kinds[string(domain.BedMedicalAction)] == actions {
			return true
		}
	}
	return false
}

type hospitalBed struct {
	Bed      string `json:"bed"`
	Room     string `json:"room"`
	RoomRole string `json:"room_role"`
}

// hospitalBeds lists, from the live room and building censuses, every
// medical humanlike bed standing inside a room whose role hosts Hospital.
func hospitalBeds(ctx context.Context, h *na.Harness, label string) ([]hospitalBed, error) {
	rooms, err := h.Call(ctx, label+"-rooms", "home/list_rooms", map[string]any{"cells": true})
	if err != nil {
		return nil, err
	}
	if success, _ := na.AsBool(rooms["success"]); !success {
		return nil, fmt.Errorf("home/list_rooms refused")
	}
	hospital, err := policy.Facility(policy.RoomRoleHospital)
	if err != nil {
		return nil, err
	}
	type cell struct{ x, z float64 }
	hostingCells := map[cell]string{}
	roleByRoom := map[string]string{}
	for _, raw := range na.AsSlice(rooms["rooms"]) {
		row, _ := na.AsMap(raw)
		id, role := fmt.Sprint(row["id"]), na.AsString(row["role"])
		roleByRoom[id] = role
		if !hospital.Hosts(policy.RoomRole(role)) {
			continue
		}
		for _, c := range na.AsSlice(row["cells"]) {
			p, _ := na.AsMap(c)
			hostingCells[cell{na.AsNumber(p["x"]), na.AsNumber(p["z"])}] = id
		}
	}
	buildings, err := h.Call(ctx, label+"-buildings", "home/list_buildings", map[string]any{"status": "built", "playerOnly": true, "aggregate": false})
	if err != nil {
		return nil, err
	}
	var hosted []hospitalBed
	for _, raw := range na.AsSlice(buildings["buildings"]) {
		row, _ := na.AsMap(raw)
		medical, known := na.AsBool(row["medical"])
		if !known || !medical {
			continue
		}
		position, _ := na.AsMap(row["position"])
		room, hosting := hostingCells[cell{na.AsNumber(position["x"]), na.AsNumber(position["z"])}]
		if !hosting {
			continue
		}
		hosted = append(hosted, hospitalBed{Bed: na.AsString(row["thingId"]), Room: room, RoomRole: roleByRoom[room]})
	}
	return hosted, nil
}

// audit lets the game run after the service has stopped and waits for a
// seeded patient to lie in a hosted medical bed, then pauses it again.
func auditHospital(ctx context.Context, h *na.Harness, report na.Report, patients []string, settle, poll time.Duration) error {
	hosted, err := hospitalBeds(ctx, h, "audit")
	if err != nil {
		return err
	}
	report["hospital_beds"] = hosted
	if len(hosted) == 0 {
		return fmt.Errorf("no medical bed stands inside a room hosting the Hospital facility")
	}
	// The killed service's grant may still hold the clock; release it so
	// the patients can walk to bed under the game's own AI.
	identityReply, err := h.Wire(ctx, "audit-identity", "lifecycle_read_identity", map[string]any{})
	if err != nil {
		return err
	}
	_, loaded, err := na.Outcome(identityReply, "loaded")
	if err != nil {
		return err
	}
	loadedContext, _ := na.AsMap(loaded["context"])
	if revoked, err := na.ReleaseAuthority(ctx, h, loadedContext["identity"].(map[string]any)); err != nil {
		return err
	} else if revoked != nil {
		report["authority_released"] = revoked
	}
	if _, err := h.Call(ctx, "audit-run", "rimworld/set_time_speed", map[string]any{"speed": "Fast", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	defer h.Call(context.WithoutCancel(ctx), "audit-pause", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}) //nolint:errcheck
	// list_buildings names a thing by its bare id, list_pawns by its load id.
	bedByID := map[string]hospitalBed{}
	for _, bed := range hosted {
		bedByID[strings.TrimPrefix(bed.Bed, "Thing_")] = bed
	}
	deadline := time.Now().Add(settle)
	var last []map[string]any
	for {
		listed, err := h.Call(ctx, "audit-pawns", "home/list_pawns", map[string]any{"colonistsOnly": true, "health": true})
		if err != nil {
			return err
		}
		last = last[:0]
		for _, raw := range na.AsSlice(listed["pawns"]) {
			row, _ := na.AsMap(raw)
			id := na.AsString(row["thingId"])
			if !na.Contains(patients, id) {
				continue
			}
			health, _ := na.AsMap(row["health"])
			inBed, _ := na.AsBool(health["inBed"])
			bedID := na.AsString(health["bedThingId"])
			last = append(last, map[string]any{"patient": id, "in_bed": inBed, "bed": bedID, "should_seek_medical_rest": health["shouldSeekMedicalRest"]})
			if bed, hosting := bedByID[strings.TrimPrefix(bedID, "Thing_")]; inBed && hosting {
				report["patient_in_hospital_bed"] = map[string]any{"patient": id, "bed": bed}
				report["patients"] = last
				return nil
			}
		}
		if time.Now().After(deadline) {
			report["patients"] = last
			return fmt.Errorf("no seeded patient lay in a hosted medical bed within %s", settle)
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(poll):
		}
	}
}
