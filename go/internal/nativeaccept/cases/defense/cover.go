package defense

import (
	"context"
	"errors"
	"fmt"
	"os"
	"regexp"
	"sort"
	"strconv"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// defense/cover (#581, #620): on the committed layout the fixture stages
// raider cover ahead of Entry inside the firing line's engagement zone
// (three mature oaks, two granite chunks, a lone granite rock and an
// unowned wall segment, ordinary map things) and a small ground raid that
// walks in through the corridor toward Entry. The service fights the raid
// from the line (the ActiveCombat goal recovers, the drafts are released),
// then the layout planner must recompute the approaches from the fresh
// census with the raid's crossing ranking a sector, identify every staged
// thing as cover and order its clearance (cut, haul, mine, deconstruct) as
// cover_clearance methods under EnsureDefensiveLayout. The run waits for
// every clearance to complete, then asserts natively that no staged thing
// still stands on its cell and that the planner saw the arrival.
const coverTimeout = 15 * time.Minute

// coverPrefix is the layout planner's cover method prefix
// (buildingruntime.defenseCoverPrefix).
const coverPrefix = "defense-cover-"

// coverStaged is the fixture's count of staged cover things.
const coverStaged = 7

func awayFromHome(toward domain.Rotation) (int, int) {
	switch toward {
	case domain.North:
		return 0, -1
	case domain.East:
		return -1, 0
	case domain.South:
		return 0, 1
	}
	return 1, 0
}

// coverThing is one staged cover thing: its definition and the designation
// the fixture expects the planner to order for it.
type coverThing struct{ def, designation string }

func runCover(ctx context.Context, closeClient func() error, reopenFixture func() error,
	fixture func(string, map[string]any) (map[string]any, error), launch func(string) (*service, error),
	layout store.DefenseLayoutRecord, report na.Report) error {
	// The checkpoint carries a wound that creeps into a CriticalMedical
	// hold, which suspends the priority-3 layout goal; the colony is healed
	// before the cover is staged, as the hostile-building cases do.
	healed, err := fixture("heal-before-cover", map[string]any{"op": "heal"})
	if err != nil {
		return err
	}
	report["healed_before_cover"] = healed
	dx, dz := awayFromHome(layout.Toward)
	staged, err := fixture("cover", map[string]any{"op": "cover", "x": int(layout.Entry.X), "z": int(layout.Entry.Z), "dx": dx, "dz": dz})
	if err != nil {
		return err
	}
	report["cover_staged"] = staged
	want := map[string]coverThing{}
	stagedAt := map[string][2]int64{}
	for _, row := range na.AsSlice(staged["cover"]) {
		m, _ := na.AsMap(row)
		want[na.AsString(m["id"])] = coverThing{def: na.AsString(m["def"]), designation: na.AsString(m["designation"])}
		x := int64(na.AsNumber(m["x"]))
		z := int64(na.AsNumber(m["z"]))
		stagedAt[na.AsString(m["id"])] = [2]int64{x, z}
	}
	if len(want) != coverStaged {
		return fmt.Errorf("fixture staged %d cover things, want %d: %#v", len(want), coverStaged, staged)
	}
	designations := map[string]int{}
	for _, thing := range want {
		designations[thing.designation]++
	}
	for _, d := range []string{domain.CoverClearanceCutPlant, domain.CoverClearanceHaul, domain.CoverClearanceMine, domain.CoverClearanceDeconstruct} {
		if designations[d] == 0 {
			return fmt.Errorf("fixture staged no %s cover: %v", d, designations)
		}
	}
	// The raid walks in from the map edge nearest Entry so its trail
	// crosses the census through the corridor. The ring stops here: a
	// resume would replay the pre-raid audits (#330).
	na.CapCheckpoints("raid staged; a resume replays the pre-raid audits, so no entry is taken after this point (#330)")
	raid, err := fixture("raid", map[string]any{"op": "raid", "strategy": "ImmediateAttack", "arrival": "EdgeWalkIn", "x": int(layout.Entry.X), "z": int(layout.Entry.Z)})
	if err != nil {
		return err
	}
	report["raid_incident"] = raid
	if ok, _ := na.AsBool(raid["success"]); !ok {
		return fmt.Errorf("raid fixture staged no raid: %#v", raid)
	}
	if err := closeClient(); err != nil {
		return err
	}
	svc, err := launch("cover")
	if err != nil {
		return err
	}
	defer svc.stop()
	hold, err := waitCombatMethod(ctx, svc.store, svc.wait(coverTimeout), report)
	if err != nil {
		return fmt.Errorf("combat response: %w", err)
	}
	resolved, err := waitRaidResolved(ctx, svc.store, hold.Plan, svc.wait(coverTimeout))
	report["cover_raid"] = resolved
	if err != nil {
		return err
	}
	method, err := waitCoverMethod(ctx, svc.store, svc.wait(coverTimeout), report)
	if err != nil {
		return err
	}
	cleared, err := waitCoverCleared(ctx, svc.store, method, want, svc.wait(coverTimeout))
	report["cover_plans"] = cleared
	if err != nil {
		return err
	}
	svc.stop()
	report["cover_authority"] = svc.keepAlive.snapshot()
	arrivals, err := coverArrivals(svc.StderrPath())
	report["cover_arrivals"] = arrivals
	if err != nil {
		return err
	}
	if err := reopenFixture(); err != nil {
		return err
	}
	after, err := fixture("inspect-after-cover", map[string]any{"op": "inspect"})
	if err != nil {
		return err
	}
	report["inspect_after_cover"] = after
	// A cut tree, a mined rock and a deconstructed wall are destroyed; a
	// hauled chunk still exists in the dump stockpile, so the assertion is
	// that nothing stands on its staged cell.
	var standing []string
	for _, row := range na.AsSlice(after["cover"]) {
		m, _ := na.AsMap(row)
		destroyed, _ := na.AsBool(m["destroyed"])
		spawned, _ := na.AsBool(m["spawned"])
		x := int64(na.AsNumber(m["x"]))
		z := int64(na.AsNumber(m["z"]))
		if !destroyed && spawned && stagedAt[na.AsString(m["id"])] == [2]int64{x, z} {
			standing = append(standing, na.AsString(m["id"]))
		}
	}
	if len(standing) > 0 {
		return fmt.Errorf("staged cover still standing after clearance: %v", standing)
	}
	if drafted := draftedColonists(after); len(drafted) > 0 {
		return fmt.Errorf("colonists left drafted: %v", drafted)
	}
	return nil
}

// coverDemandLine matches the planner's approach summary
// (buildingruntime.clearCover): the sector count, the ranked sector's
// recent raids and the arrivals the census carried.
var coverDemandLine = regexp.MustCompile(`defense-layout: cover demand .*sectors (\d+), ranked_sector_raids (\d+), arrivals (\d+)`)

// coverArrivals reads the service log for the last approach summary the
// planner logged and requires the staged raid to have been an arrival that
// ranked a sector: the crossing the native trail recorded reached the
// policy, not just the census.
func coverArrivals(logPath string) (map[string]any, error) {
	data, err := os.ReadFile(logPath)
	if err != nil {
		return nil, fmt.Errorf("read service log: %w", err)
	}
	matches := coverDemandLine.FindAllStringSubmatch(string(data), -1)
	if len(matches) == 0 {
		return nil, errors.New("the planner logged no cover approach summary")
	}
	last := matches[len(matches)-1]
	sectors, _ := strconv.Atoi(last[1])
	ranked, _ := strconv.Atoi(last[2])
	arrivals, _ := strconv.Atoi(last[3])
	out := map[string]any{"summaries": len(matches), "sectors": sectors, "ranked_sector_raids": ranked, "arrivals": arrivals, "line": last[0]}
	if arrivals == 0 || ranked == 0 {
		return out, fmt.Errorf("the staged raid ranked no sector: %s", last[0])
	}
	return out, nil
}

// waitCoverMethod polls the journal for the layout goal's first cover
// clearance method.
func waitCoverMethod(ctx context.Context, s *store.Store, w na.Wait, report na.Report) (domain.GoalMethod, error) {
	var found domain.GoalMethod
	err := na.WaitProgress(ctx, w, func(ctx context.Context) (string, bool, error) {
		review, err := s.LoadRoutineReview(ctx)
		if err != nil {
			return na.Signature("no-review"), false, nil
		}
		for _, binding := range review.Goals {
			if binding.Need != policy.EnsureDefensiveLayout {
				continue
			}
			goal, err := s.LoadGoal(ctx, binding.Goal)
			if err != nil && !errors.Is(err, store.ErrNotFound) {
				return "", false, err
			}
			var methods []string
			for _, m := range goal.Methods {
				methods = append(methods, string(m.Method))
				if strings.HasPrefix(string(m.Method), coverPrefix) {
					report["cover_goal"] = string(binding.Goal)
					report["cover_method"] = string(m.Method)
					found = m
					return "", true, nil
				}
			}
			return na.Signature("bound", binding.Goal, goal.Goal.Epoch, methods), false, nil
		}
		return na.Signature("unbound", len(review.Goals)), false, nil
	})
	if err != nil {
		return domain.GoalMethod{}, fmt.Errorf("no cover clearance method: %w", err)
	}
	return found, nil
}

// waitCoverCleared waits until every staged thing has a completed
// clearance under the goal's cover methods, each with the designation its
// kind takes. The map's own cover inside the engagement zone rides along in
// the same batches and is not asserted. The first method found is followed
// by its plan: once the deficit closes the goal recovers and retires its
// methods, so the goal's list alone would lose the plan that did the work.
func waitCoverCleared(ctx context.Context, s *store.Store, first domain.GoalMethod, want map[string]coverThing, w na.Wait) (map[string]any, error) {
	var out map[string]any
	err := na.WaitProgress(ctx, w, func(ctx context.Context) (string, bool, error) {
		goal, err := s.LoadGoal(ctx, first.Goal)
		if err != nil && !errors.Is(err, store.ErrNotFound) {
			return "", false, err
		}
		plans := map[domain.PlanID]domain.GoalMethod{first.Plan: first}
		for _, m := range goal.Methods {
			if strings.HasPrefix(string(m.Method), coverPrefix) {
				plans[m.Plan] = m
			}
		}
		ids := make([]string, 0, len(plans))
		for id := range plans {
			ids = append(ids, string(id))
		}
		sort.Strings(ids)
		staged := map[string]string{}
		var methods []string
		var signature []string
		for _, id := range ids {
			m := plans[domain.PlanID(id)]
			methods = append(methods, string(m.Method))
			state, err := s.LoadPlan(ctx, m.Plan)
			if err != nil {
				return "", false, err
			}
			signature = append(signature, na.PlanSignature(state))
			for _, p := range state.Progress {
				clearance, ok := p.Action().CoverClearance()
				if !ok {
					return "", false, fmt.Errorf("cover plan %s carries a %s action", m.Plan, p.Action().Kind())
				}
				thing, isStaged := want[clearance.Thing()]
				if !isStaged {
					continue
				}
				if clearance.Definition() != thing.def || clearance.Designation() != thing.designation {
					return "", false, fmt.Errorf("cover plan orders %s on %s (%s), want %s on %s", clearance.Designation(), clearance.Thing(), clearance.Definition(), thing.designation, thing.def)
				}
				v := p.View()
				if v.Stage == domain.Unsuccessful || v.Stage == domain.Cancelled {
					return "", false, fmt.Errorf("clearance of %s settled %s: %v", clearance.Thing(), v.Stage, stages(state.Progress))
				}
				staged[clearance.Thing()] = string(v.Stage)
			}
		}
		out = map[string]any{"methods": methods, "staged": staged}
		done := len(staged) == len(want)
		for _, stage := range staged {
			done = done && stage == string(domain.Completed)
		}
		return na.Signature("cover", methods, staged, signature), done, nil
	})
	if err != nil {
		return out, fmt.Errorf("cover not cleared: %w", err)
	}
	return out, nil
}

func draftedColonists(inspect map[string]any) []string {
	var drafted []string
	for _, row := range na.AsSlice(inspect["colonists"]) {
		m, _ := na.AsMap(row)
		if d, _ := na.AsBool(m["drafted"]); d {
			drafted = append(drafted, na.AsString(m["id"]))
		}
	}
	return drafted
}
