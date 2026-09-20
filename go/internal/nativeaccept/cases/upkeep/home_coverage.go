package upkeep

import (
	"context"
	"encoding/json"
	"fmt"
	"sort"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/sustained"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// The fixture provides two empty bed chambers and a connecting corridor.
// Both beds must be built through routine plans before Home can be maintained.
func init() {
	sleeping := scenarios()["sleeping"]
	cases.Register(cases.Case{
		Name:   "upkeep/home-coverage",
		Scope:  "Connected autonomous Home (#452): two routine-built beds, corridor coverage, stale revision/geometry refusal, restoration after removal and save recovery.",
		Start:  cases.Save{Name: sustained.BaselineSave},
		Keep:   sleeping.keep,
		Serve:  &cases.ServeSpec{Families: []string{"sleeping", "home-coverage", "work"}, Extra: sleeping.extra, Prefix: prefix},
		Budget: 12 * time.Minute,
		Reason: "Two small beds establish real ownership; subsequent Home writes require only routine reviews on the same colony.",
		Run:    runHomeCoverage,
	})
}

func runHomeCoverage(ctx context.Context, s cases.Session) error {
	report, identity := s.Report(), s.Identity()
	for _, fixture := range []string{"test/sleeping_setup", "test/home_coverage_setup", "test/home_coverage_read", "test/home_remove_cell", "test/home_cell_read", "test/home_stale_guards"} {
		if !na.Contains(s.Names(), fixture) {
			return fmt.Errorf("missing %s", fixture)
		}
	}
	h := s.Harness()
	prepared, err := callFixture(ctx, h, identity, "test/sleeping_setup", map[string]any{"connectedRooms": true})
	if err != nil {
		return err
	}
	report["prepared"] = prepared
	before, err := readBedIDs(ctx, h, identity, "beds-before")
	if err != nil {
		return err
	}
	spec := s.Spec()
	spec.Families = []string{"sleeping", "work"}
	service, err := s.Serve(ctx, spec)
	if err != nil {
		return err
	}
	bedReport := na.Report{}
	report["stage_beds"] = bedReport
	journal, err := serveStage(ctx, service, bedReport)
	if err == nil {
		err = watchHomeBeds(ctx, journal, bedReport)
	}
	service.Stop()
	if err != nil {
		return err
	}
	h, err = reattachPaused(ctx, s)
	if err != nil {
		return err
	}
	after, err := readBedIDs(ctx, h, identity, "beds-after")
	if err != nil {
		return err
	}
	built := []string{}
	for id, def := range after {
		if before[id] == "" && def == "Bed" {
			built = append(built, id)
		}
	}
	sort.Strings(built)
	report["beds_built"] = built
	if len(built) != 2 {
		return fmt.Errorf("expected two routine-built beds, got %v", built)
	}
	corridor, _ := na.AsMap(prepared["corridor"])
	outside, _ := na.AsMap(prepared["outside"])
	cx, cz := int(na.AsNumber(corridor["x"])), int(na.AsNumber(corridor["z"]))
	target := built[0]
	staged, err := callFixture(ctx, h, identity, "test/home_coverage_setup", map[string]any{"target": target})
	if err != nil {
		return err
	}
	report["stripped"] = staged
	total := int(na.AsNumber(staged["count"]))
	if total < 9 {
		return fmt.Errorf("connected geometry omitted chambers or corridor: %v", staged)
	}
	if err = auditHomeCells(ctx, h, cx, cz, outside, false); err != nil {
		return err
	}
	for stage := 0; stage < 2; stage++ {
		stageReport := na.Report{}
		report[fmt.Sprintf("stage_home_%d", stage)] = stageReport
		if stage == 1 {
			guards, e := callFixture(ctx, h, identity, "test/home_stale_guards", map[string]any{"target": target, "doorX": cx - 1, "doorZ": cz})
			if e != nil {
				return e
			}
			report["stale_guards"] = guards
			removed, e := callFixture(ctx, h, identity, "test/home_remove_cell", map[string]any{"target": target, "x": cx + 1, "z": cz})
			if e != nil {
				return e
			}
			report["removed"] = removed
		}
		spec = s.Spec()
		spec.Families = []string{"home-coverage", "work"}
		service, err = s.Serve(ctx, spec)
		if err != nil {
			return err
		}
		journal, err = serveStage(ctx, service, stageReport)
		if err == nil {
			err = watchConnectedHome(ctx, journal, built, stageReport)
		}
		service.Stop()
		if err != nil {
			return err
		}
		h, err = reattachPaused(ctx, s)
		if err != nil {
			return err
		}
		for _, bed := range built {
			covered, e := readHomeCoverage(ctx, h, identity, bed, fmt.Sprintf("home-%d-%s", stage, bed))
			if e != nil {
				return e
			}
			stageReport[bed] = covered
			if covered.Covered != total || covered.Total != total {
				return fmt.Errorf("connected footprint not fully Home: %+v, expected %d", covered, total)
			}
		}
		if err = auditHomeCells(ctx, h, cx, cz, outside, true); err != nil {
			return err
		}
	}
	// Controller restarts above exercise durable routine ownership. Reload the
	// final native save and independently audit the actual Home mask as well.
	save := "RimGovernor-home-connected"
	if _, err = h.Call(ctx, "save-home", "rimworld/save_game", map[string]any{"saveName": save}); err != nil {
		return err
	}
	if _, err = h.Call(ctx, "reload-home", "rimworld/load_game_ready", map[string]any{"saveName": save, "readiness": "visual", "timeoutMs": 90000, "ignoreModCompatibility": false}); err != nil {
		return err
	}
	if _, err = h.Call(ctx, "pause-home", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	if err = auditHomeCells(ctx, h, cx, cz, outside, true); err != nil {
		return err
	}
	report["save_recovery_home"] = true
	return nil
}

func watchHomeBeds(ctx context.Context, journal *store.Store, report na.Report) error {
	bounded, cancel := context.WithTimeout(ctx, 5*time.Minute)
	defer cancel()
	seen := map[domain.PlanID]bool{}
	for n := 0; n < 2; n++ {
		label := fmt.Sprintf("bed_%d", n)
		_, err := followMethodsExcluding(bounded, journal, policy.MaintainSleeping, label, seen, func(a domain.Action) error {
			b, ok := a.Building()
			if !ok || b.Definition() != "Bed" {
				return fmt.Errorf("not a bed build: %s", a.Kind())
			}
			return nil
		}, report)
		if err != nil {
			return err
		}
		if report[label+"_recovered_by"] != "controller_order" {
			return fmt.Errorf("bed %d lacks completed controller ownership", n)
		}
	}
	return nil
}

func watchConnectedHome(ctx context.Context, journal *store.Store, built []string, report na.Report) error {
	bounded, cancel := context.WithTimeout(ctx, 90*time.Second)
	defer cancel()
	if _, err := waitNeed(bounded, journal, policy.MaintainHomeCoverage, domain.NeedDeficit); err != nil {
		return err
	}
	_, err := followMethods(bounded, journal, policy.MaintainHomeCoverage, "home", func(a domain.Action) error {
		coverage, ok := a.HomeCoverage()
		if !ok || !na.Contains(built, coverage.Target()) {
			return fmt.Errorf("home method did not target a built facility")
		}
		return nil
	}, report)
	if err != nil {
		return err
	}
	if report["home_recovered_by"] != "controller_order" {
		return fmt.Errorf("home extension lacks controller completion")
	}
	_, err = waitNeed(bounded, journal, policy.MaintainHomeCoverage, domain.NeedRecovered)
	return err
}

func auditHomeCells(ctx context.Context, h *na.Harness, cx, cz int, outside map[string]any, want bool) error {
	for i := 0; i < 4; i++ {
		x, z, expected := cx+i, cz, want
		if i == 3 {
			x, z, expected = int(na.AsNumber(outside["x"])), int(na.AsNumber(outside["z"])), false
		}
		row, err := h.Call(ctx, fmt.Sprintf("home-cell-%d-%t", i, want), "test/home_cell_read", map[string]any{"x": x, "z": z})
		if err != nil {
			return err
		}
		actual, known := na.AsBool(row["home"])
		success, _ := na.AsBool(row["success"])
		if !success || !known || actual != expected {
			return fmt.Errorf("home at %d,%d: %v, want %t", x, z, row, expected)
		}
	}
	return nil
}

// serveStage takes the service through authority to its first routine
// review, refusing a roll that holds development as an emergency.
func serveStage(ctx context.Context, service *na.ServiceProcess, report na.Report) (*store.Store, error) {
	rootPlanID, err := service.Acquire()
	if err != nil {
		return nil, err
	}
	report["root_plan"] = rootPlanID
	service.KeepAuthority(ctx)
	journal, err := service.Store(ctx)
	if err != nil {
		return nil, err
	}
	review, diagnostics, err := service.WaitRoutineReview(ctx, journal, 90*time.Second)
	report["diagnostic_post_acquire"] = diagnostics
	if err != nil {
		return nil, err
	}
	reviewData, _ := json.Marshal(review)
	report["routine_review_first"] = json.RawMessage(reviewData)
	for _, row := range review.Development.Rows {
		if row.Reason == policy.DevelopmentEmergency {
			return nil, fmt.Errorf("first review holds development as an emergency (patients %v); the fixture roll is unusable", review.MedicalCare.Patients)
		}
	}
	return journal, nil
}

// readBedIDs maps every colonist bed on the map to its defName.
func readBedIDs(ctx context.Context, h *na.Harness, identity map[string]any, label string) (map[string]string, error) {
	observed, err := readColonyFacts(ctx, h, identity, label)
	if err != nil {
		return nil, err
	}
	section, _ := na.AsMap(observed["upkeep"])
	_, upkeep, err := na.Outcome(section, "observed")
	if err != nil {
		return nil, fmt.Errorf("%s: upkeep facts unavailable: %w", label, err)
	}
	ids := map[string]string{}
	for _, raw := range na.AsSlice(upkeep["beds"]) {
		row, _ := na.AsMap(raw)
		ref, _ := na.AsMap(row["bed"])
		if id := na.AsString(ref["id"]); id != "" {
			ids[id] = na.AsString(ref["defName"])
		}
	}
	return ids, nil
}

// homeCoverageRow returns the upkeep census's Home coverage row for target;
// native includes covered targets so complete extent geometry remains available.
func homeCoverageRow(observed map[string]any, target string) (map[string]any, bool) {
	section, _ := na.AsMap(observed["upkeep"])
	_, upkeep, err := na.Outcome(section, "observed")
	if err != nil {
		return nil, false
	}
	home, _ := na.AsMap(upkeep["homeCoverage"])
	_, facts, err := na.Outcome(home, "observed")
	if err != nil {
		return nil, false
	}
	for _, raw := range na.AsSlice(facts["targets"]) {
		row, _ := na.AsMap(raw)
		if na.AsString(row["id"]) == target {
			return row, true
		}
	}
	return nil, false
}

type homeCoverageRead struct {
	Covered  int   `json:"covered"`
	Total    int   `json:"total"`
	Revision int64 `json:"revision"`
}

// readHomeCoverage is the independent native Home read for one facility.
func readHomeCoverage(ctx context.Context, h *na.Harness, identity map[string]any, target, label string) (homeCoverageRead, error) {
	reply, err := h.Call(ctx, label, "test/home_coverage_read", map[string]any{"target": target})
	if err != nil {
		return homeCoverageRead{}, err
	}
	if success, _ := na.AsBool(reply["success"]); !success {
		return homeCoverageRead{}, fmt.Errorf("%s: no bounded native scope for %s: %#v", label, target, reply)
	}
	return homeCoverageRead{Covered: int(na.AsNumber(reply["covered"])), Total: int(na.AsNumber(reply["total"])), Revision: int64(na.AsNumber(reply["revision"]))}, nil
}
