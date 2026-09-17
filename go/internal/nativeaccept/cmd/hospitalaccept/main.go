// Command hospitalaccept is issue #4 M3's clinic acceptance: load a save,
// seed two flu patients who may seek bed rest (test/medical_management_setup
// with hospital=true: no pre-placed medical spots), run the service with the
// tend, rescue, medicine-reserve and hospital families composed over the
// startup ladder, and watch MaintainMedicalCare until the hospital planner's
// bed_medical patch completes or the window ends. Acceptance is audited
// against live native facts, not the journal: a humanlike bed must read
// medical inside a room whose native Room.Role hosts the Hospital facility
// (Bedroom, Barracks or Room per policy.FacilityCatalog -- a mixed-bed room
// never scores Hospital itself), and one of the seeded patients must be
// lying in that bed, tended or resting, within the settle window after the
// service stops. A receipt, a blueprint or a journal proof alone never passes.
//
// The default save is the tribal8 baseline the other facility harnesses
// use; -save selects another existing save. Like workshopaccept this runs
// the real serve binary and reads its durable journal, so it needs a
// prebuilt rimgovernor binary and the current native mod installed.
package main

import (
	"context"
	"flag"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/sustainedfood"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

const baselineSave = "RimGovernor-tribal8-baseline"

// hospitalFamilies composes facilityaccept's startup ladder plus the
// families a MaintainMedicalCare deficit walks through: tend and rescue own
// the patients, medical the medicine reserve, hospital the hosted bed, work
// the Doctor coverage the reserve and tending need.
const hospitalFamilies = "sleeping,shelter,temperature,comfort,work,supply,tend,rescue,medical,hospital"

func main() {
	root := flag.String("root", "", "absolute disposable worker root (e.g. .rimgovernor/bridge)")
	output := flag.String("output", "", "fresh output directory (default <root>/native-hospital-acceptance)")
	rendered := flag.Bool("rendered", false, "use the windowed profile instead of headless")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	rimgovernorBinary := flag.String("rimgovernor", "", "absolute path to a prebuilt rimgovernor binary (go build ./go/cmd/rimgovernor)")
	save := flag.String("save", baselineSave, "existing save name to load")
	watch := flag.Duration("watch", 25*time.Minute, "wall-clock duration to observe MaintainMedicalCare before giving up")
	poll := flag.Duration("poll", 5*time.Second, "sampling interval during the watch window")
	settle := flag.Duration("settle", 4*time.Minute, "wall-clock window after the service stops for a patient to reach the hospital bed")
	timeout := flag.Duration("timeout", 40*time.Minute, "overall run timeout (must exceed -watch plus -settle plus startup/shutdown)")
	nativeTimeout := flag.Duration("native-timeout", 15*time.Second, "serve subprocess's own --timeout")
	clockSpeed := flag.String("clock-speed", "Superfast", "serve's --clock-speed (Normal, Fast or Superfast)")
	families := flag.String("families", hospitalFamilies, "RIMGOVERNOR_ROUTINE_FAMILIES composition for the run")
	flightRecorder := flag.Bool("flight-recorder", false, "record every native request/response of the service under <output>/flight-recorder.jsonl")
	flag.Parse()
	if *root == "" || *rimgovernorBinary == "" || !filepath.IsAbs(*rimgovernorBinary) {
		fmt.Fprintln(os.Stderr, "-root and an absolute -rimgovernor are required")
		os.Exit(2)
	}
	if *output == "" {
		*output = *root + "/native-hospital-acceptance"
	}
	if err := os.MkdirAll(*output, 0755); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}
	if entries, _ := os.ReadDir(*output); len(entries) > 0 {
		fmt.Fprintln(os.Stderr, "-output must be a fresh, empty directory")
		os.Exit(2)
	}
	report := na.NewReport("MaintainMedicalCare's patients get a medical bed inside a room whose native role hosts the Hospital facility, and a seeded flu patient lies in it; the bed's medical flag and the patient's bed are read live, never from receipts (issue #4, M3).", !*rendered)
	ctx, cancel := context.WithTimeout(context.Background(), *timeout)
	defer cancel()
	var patients []string
	cfg := sustainedfood.RunConfig{
		Root: *root, Output: *output, GameID: *game, Headless: !*rendered,
		RimgovernorBinary: *rimgovernorBinary, Save: *save,
		Watch: *watch, Poll: *poll, NativeTimeout: *nativeTimeout, ClockSpeed: *clockSpeed,
		RequestPrefix: "hospital", Families: *families, Goal: policy.MaintainMedicalCare,
		Until: bedConverted,
	}
	if *flightRecorder {
		cfg.ServeArgs = []string{"--flight-recorder", filepath.Join(*output, "flight-recorder.jsonl")}
	}
	cfg.Prepare = func(ctx context.Context, h *na.Harness, report na.Report) error {
		prepared, err := h.Call(ctx, "medical-setup", "test/medical_management_setup", map[string]any{
			"disease": true, "failSurgery": false, "manualTending": false, "withdrawal": false, "hospital": true,
		})
		if err != nil {
			return err
		}
		if success, _ := na.AsBool(prepared["success"]); !success {
			return fmt.Errorf("medical_management_setup refused: %#v", prepared)
		}
		report["medical_prepared"] = prepared
		for _, raw := range na.AsSlice(prepared["patients"]) {
			patients = append(patients, na.AsString(raw))
		}
		if len(patients) != 2 {
			return fmt.Errorf("medical_management_setup seeded %d patients, want 2", len(patients))
		}
		hosted, err := hospitalBeds(ctx, h, "baseline")
		if err != nil {
			return err
		}
		report["baseline_hospital_beds"] = hosted
		if len(hosted) > 0 {
			return fmt.Errorf("save already holds a hosted medical bed; nothing for the hospital planner to provide")
		}
		return nil
	}
	cfg.Audit = func(ctx context.Context, h *na.Harness, report na.Report) error {
		return audit(ctx, h, report, patients, *settle, *poll)
	}
	timeline, err := sustainedfood.Run(ctx, cfg, report)
	report["timeline_samples"] = len(timeline)
	if err != nil {
		report["error"] = err.Error()
	} else {
		report["passed"] = true
	}
	os.Exit(report.Finalize(*output))
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
func audit(ctx context.Context, h *na.Harness, report na.Report, patients []string, settle, poll time.Duration) error {
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
