package upkeep

import (
	"context"
	"encoding/json"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/sustained"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// upkeep/home-coverage (issue #292): MaintainHomeCoverage extends native Home
// over a facility the controller built. Ownership is the journal's own
// construction lineage, so the case first has MaintainSleeping build the
// missing bed of test/sleeping_setup's room (stage one, the sleeping
// scenario's bed method), then takes the slot back, strips Home from the
// bed's bounded footprint (the bed plus its enclosed roofed room,
// test/home_coverage_setup) and serves again with the home-coverage family:
// the goal must open on the deficit, dispatch one ExtendHome method for the
// bed under the fixture's shape token, and recover on the observed Home
// cells, which test/home_coverage_read audits after the service releases
// the game.
const homeCoverageBudget = 20 * time.Minute

func init() {
	sleeping := scenarios()["sleeping"]
	cases.Register(cases.Case{
		Name: "upkeep/home-coverage",
		Scope: "Home coverage upkeep (issue #292) on the " + sustained.BaselineSave + " save: MaintainSleeping builds one bed the controller then owns, " +
			"test/home_coverage_setup removes Home over its footprint, and the service composed with the home-coverage family follows the journal " +
			"from deficit through one typed ExtendHome method to observed recovery, audited by an independent native Home read after the service releases the game.",
		Start:  cases.Save{Name: sustained.BaselineSave},
		Keep:   sleeping.keep,
		Serve:  &cases.ServeSpec{Families: []string{"sleeping", "home-coverage", "work"}, Extra: sleeping.extra, Prefix: prefix},
		Budget: homeCoverageBudget,
		Reason: "Two served stages: the controller must first build the facility it will own (a bed, one to four minutes), and the Home extension is followed on a second service over the same journal.",
		Run:    runHomeCoverage,
	})
}

func runHomeCoverage(ctx context.Context, s cases.Session) error {
	report, identity := s.Report(), s.Identity()
	sleeping := scenarios()["sleeping"]
	for _, fixture := range []string{sleeping.fixture, "test/home_coverage_setup", "test/home_coverage_read"} {
		if !na.Contains(s.Names(), fixture) {
			return fmt.Errorf("missing %s in discovery; rebuild the native mod with -Fixture UpkeepFixture,ForecastFixture,RoutineSleepingFixture", fixture)
		}
	}
	h := s.Harness()

	// Stage one: the sleeping fixture and the bed MaintainSleeping builds.
	bedReport := na.Report{}
	report["stage_bed"] = bedReport
	prepared, err := sleeping.prepare(ctx, h, identity, bedReport)
	if err != nil {
		return err
	}
	bedReport["prepared"] = prepared
	bedsBefore, err := readBedIDs(ctx, h, identity, "beds-before")
	if err != nil {
		return err
	}
	spec := s.Spec()
	spec.Families = []string{"sleeping", "work"}
	service, err := s.Serve(ctx, spec)
	if err != nil {
		return err
	}
	journal, err := serveStage(ctx, service, bedReport)
	if err != nil {
		service.Stop()
		return err
	}
	bedErr := watchBedBuilt(ctx, journal, bedReport)
	bedReport["authority_reacquisitions"] = service.Stop()
	afterCtx := ctx
	if ctx.Err() != nil {
		var cancel context.CancelFunc
		afterCtx, cancel = context.WithTimeout(context.Background(), 90*time.Second)
		defer cancel()
	}
	if h, err = reattachPaused(afterCtx, s); err != nil {
		return fmt.Errorf("stage bed: %w", err)
	}
	if bedErr != nil {
		return fmt.Errorf("stage bed: %w", bedErr)
	}
	bedsAfter, err := readBedIDs(ctx, h, identity, "beds-after")
	if err != nil {
		return err
	}
	// The method's bed is the one real Bed that is new; a sleeping spot the
	// service also placed (#218) is not the owned facility under test.
	var built, spots []string
	for id, definition := range bedsAfter {
		switch {
		case bedsBefore[id] != "":
		case definition == "SleepingSpot":
			spots = append(spots, id)
		default:
			built = append(built, id)
		}
	}
	bedReport["beds_built"] = built
	bedReport["sleeping_spots_built"] = spots
	if len(built) != 1 {
		return fmt.Errorf("stage bed: expected exactly one new bed, found %v", built)
	}
	bed := built[0]

	// Stage two: Home stripped from the bed's footprint, then the extension.
	homeReport := na.Report{}
	report["stage_home"] = homeReport
	staged, err := callFixture(ctx, h, identity, "test/home_coverage_setup", map[string]any{"target": bed})
	if err != nil {
		return err
	}
	homeReport["prepared"] = staged
	shape := na.AsString(staged["shape"])
	stagedRevision := int64(na.AsNumber(staged["revision"]))
	footprint := int(na.AsNumber(staged["count"]))
	if shape == "" || footprint <= 0 {
		return fmt.Errorf("stage home: fixture staged no footprint for %s: %#v", bed, staged)
	}
	before, err := readHomeCoverage(ctx, h, identity, bed, "home-before")
	if err != nil {
		return err
	}
	homeReport["home_before"] = before
	if before.Covered != 0 || before.Total != footprint {
		return fmt.Errorf("stage home: %d of %d footprint cells are Home after the fixture removed them", before.Covered, before.Total)
	}
	spec = s.Spec()
	service, err = s.Serve(ctx, spec)
	if err != nil {
		return err
	}
	journal, err = serveStage(ctx, service, homeReport)
	if err != nil {
		service.Stop()
		return err
	}
	homeErr := watchHomeCoverage(ctx, journal, bed, shape, homeReport)
	if homeErr == nil {
		homeErr = na.AssertRoutineRunning(service.Get)
	}
	homeReport["authority_reacquisitions"] = service.Stop()
	afterCtx = ctx
	if ctx.Err() != nil {
		var cancel context.CancelFunc
		afterCtx, cancel = context.WithTimeout(context.Background(), 90*time.Second)
		defer cancel()
	}
	if h, err = reattachPaused(afterCtx, s); err != nil {
		return fmt.Errorf("stage home: %w", err)
	}
	after, readErr := readHomeCoverage(afterCtx, h, identity, bed, "home-after")
	if readErr == nil {
		homeReport["home_after"] = after
	}
	if homeErr != nil {
		return fmt.Errorf("stage home: %w", homeErr)
	}
	if readErr != nil {
		return readErr
	}
	if after.Total != footprint || after.Covered != after.Total {
		return fmt.Errorf("stage home: %d of %d footprint cells are Home after MaintainHomeCoverage recovered", after.Covered, after.Total)
	}
	if after.Revision <= stagedRevision {
		return fmt.Errorf("stage home: Home revision %d did not advance past the fixture's %d", after.Revision, stagedRevision)
	}
	census, err := readColonyFacts(ctx, h, identity, "upkeep-after")
	if err != nil {
		return err
	}
	if row, present := homeCoverageRow(census, bed); present {
		return fmt.Errorf("stage home: the upkeep census still lists %s as a Home deficit: %#v", bed, row)
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

// watchBedBuilt follows MaintainSleeping's first method, the bed build, to
// its completed plan; ownership and sleep are the sleeping case's property,
// not this one's, so the stage ends once the controller owns a building.
func watchBedBuilt(ctx context.Context, journal *store.Store, report na.Report) error {
	deficitCtx, deficitCancel := context.WithTimeout(ctx, 4*time.Minute)
	defer deficitCancel()
	if _, err := waitNeed(deficitCtx, journal, policy.MaintainSleeping, domain.NeedDeficit); err != nil {
		return err
	}
	if _, err := followMethods(ctx, journal, policy.MaintainSleeping, "bed", func(a domain.Action) error {
		if _, ok := a.Building(); ok {
			return nil
		}
		return fmt.Errorf("unexpected %s action before a bed was built", a.Kind())
	}, report); err != nil {
		return err
	}
	if report["bed_recovered_by"] != "controller_order" {
		return fmt.Errorf("the bed was not built by the controller's own order (%v), so no construction lineage owns it", report["bed_recovered_by"])
	}
	return nil
}

// watchHomeCoverage follows MaintainHomeCoverage from deficit through one
// ExtendHome method for the staged bed to recovery.
func watchHomeCoverage(ctx context.Context, journal *store.Store, bed, shape string, report na.Report) error {
	deficitCtx, deficitCancel := context.WithTimeout(ctx, 4*time.Minute)
	defer deficitCancel()
	if _, err := waitNeed(deficitCtx, journal, policy.MaintainHomeCoverage, domain.NeedDeficit); err != nil {
		return err
	}
	if _, err := followMethods(ctx, journal, policy.MaintainHomeCoverage, "home", func(a domain.Action) error {
		coverage, ok := a.HomeCoverage()
		if !ok {
			return fmt.Errorf("not a home coverage action: %v", a.Kind())
		}
		if coverage.Target() != bed {
			return fmt.Errorf("home coverage targets %s, not the built bed %s", coverage.Target(), bed)
		}
		if coverage.Shape() != shape {
			return fmt.Errorf("home coverage shape %s differs from the fixture's %s", coverage.Shape(), shape)
		}
		return nil
	}, report); err != nil {
		return err
	}
	if report["home_recovered_by"] != "controller_order" {
		return fmt.Errorf("Home was not extended by the controller's own order (%v)", report["home_recovered_by"])
	}
	recoverCtx, recoverCancel := context.WithTimeout(ctx, 6*time.Minute)
	defer recoverCancel()
	goal, err := waitNeed(recoverCtx, journal, policy.MaintainHomeCoverage, domain.NeedRecovered)
	if err != nil {
		return err
	}
	report["home_recovered_tick"] = int64(goal.Goal.Tick)
	return nil
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
// native lists only targets with missing cells or a blocker.
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
