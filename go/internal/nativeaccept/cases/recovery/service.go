// The recovery/service case exercises the disaster-recovery service vertical
// (issue #27) through GiveJobIntent Repair on Actions/Apply (#940,
// NativeRepairOperations.cs). A genuinely damaged player Wall is repaired
// by a real native WorkGiver_Repair job ordered for an undrafted colonist,
// observed via real game ticks and independently confirmed via
// rimgovernor/observations_list_buildings (HitPoints == MaxHitPoints), not
// just the applied result.
//
// Uses the disposable test/recovery_service_prepare fixture
// (RecoveryServiceFixture.cs): one damaged player Wall and one capable,
// reachable colonist.
package recovery

import (
	"context"
	"fmt"
	"os"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{
		Name: "recovery/service",
		Scope: "GiveJobIntent Repair (disaster-recovery service) dispatch: a genuinely damaged player Wall is " +
			"actually repaired by a real native WorkGiver_Repair job, unknown-pawn refusal, real HitPoints change " +
			"observed via native ticks (not just an applied result), and key replay idempotency.",
		Start:  cases.Fixture{On: cases.LabStart(), Op: "test/recovery_service_prepare"},
		Budget: 5 * time.Minute,
		Run:    runService,
	})
}

func runService(ctx context.Context, s cases.Session) error {
	// Fixture: one genuinely damaged player Wall and one capable
	// Construction-enabled colonist. Run this BEFORE acquiring authority: the
	// fixture spawns/damages a building directly outside any
	// authority.Owned() scope, and NativeControlAuthority.RevokeExternal
	// revokes any held lease for such external activity regardless of who
	// holds it, mirroring animalcontainmentaccept's own ordering.
	report := s.Report()
	h := s.Harness()
	identity := s.Identity()
	prepared := s.Prepared()
	if !na.Contains(s.Names(), "rimgovernor/operations_apply") {
		return fmt.Errorf("missing rimgovernor/operations_apply in discovery")
	}
	pawnID := na.AsString(prepared["pawn"])
	wallID := na.AsString(prepared["wall"])
	if pawnID == "" || wallID == "" {
		return fmt.Errorf("prepare: missing fixture pawn/wall ids: %#v", prepared)
	}
	maxHitPoints := na.AsNumber(prepared["maxHitPoints"])
	damagedHitPoints := na.AsNumber(prepared["damagedHitPoints"])
	if maxHitPoints <= 0 || damagedHitPoints <= 0 || damagedHitPoints >= maxHitPoints {
		return fmt.Errorf("prepare: fixture wall is not genuinely damaged: %#v", prepared)
	}
	report["fixture_pawn"] = pawnID
	report["fixture_wall"] = wallID
	report["fixture_max_hit_points"] = maxHitPoints
	report["fixture_damaged_hit_points"] = damagedHitPoints

	if _, err := na.GrantAuto(ctx, h.WireFunc(), "acquire", identity); err != nil {
		return err
	}

	// wallState reads HitPoints/maxHitPoints through
	// rimgovernor/observations_list_buildings with statuses=["built"],
	// category="artificial", playerOnly=true, purely to independently assert
	// the wall's real HitPoints before/after dispatch.
	wallState := func(label string) (hitPoints, maxHP float64, err error) {
		reply, err := h.Wire(ctx, label, "observations_list_buildings", map[string]any{
			"scope": map[string]any{"expectedIdentity": identity}, "ids": []string{wallID},
			"statuses": []string{"built"}, "category": "artificial", "playerOnly": true,
		})
		if err != nil {
			return 0, 0, err
		}
		_, observed, err := na.Outcome(reply, "observed")
		if err != nil {
			return 0, 0, err
		}
		rows := na.AsSlice(observed["buildings"])
		if len(rows) != 1 {
			return 0, 0, fmt.Errorf("%s: expected exactly one building row, got %#v", label, observed)
		}
		row, _ := na.AsMap(rows[0])
		building, _ := na.AsMap(row["building"])
		if na.AsString(building["id"]) != wallID {
			return 0, 0, fmt.Errorf("%s: unexpected building row: %#v", label, row)
		}
		if _, present := row["hitPoints"]; !present {
			return 0, 0, fmt.Errorf("%s: missing hitPoints: %#v", label, row)
		}
		return na.AsNumber(row["hitPoints"]), na.AsNumber(row["maxHitPoints"]), nil
	}

	apply := func(key, pawn string) (map[string]any, error) {
		reply, err := h.Wire(ctx, key, "operations_apply", map[string]any{"identity": identity,
			"actions": []any{map[string]any{"key": key, "giveJob": na.GiveJob(pawn, "Repair", wallID)["giveJob"]}}})
		if err != nil {
			return nil, err
		}
		results := na.AsSlice(reply["results"])
		if len(results) != 1 {
			return nil, fmt.Errorf("%s: expected one result: %#v", key, reply)
		}
		result, _ := na.AsMap(results[0])
		return result, nil
	}

	beforeHitPoints, beforeMax, err := wallState("wall-before")
	if err != nil {
		return err
	}
	if beforeHitPoints >= beforeMax {
		return fmt.Errorf("wall-before: expected the fixture wall to be genuinely damaged, got hitPoints=%v maxHitPoints=%v", beforeHitPoints, beforeMax)
	}
	report["wall_before_hit_points"] = beforeHitPoints
	report["wall_max_hit_points"] = beforeMax
	unknown, err := apply("recovery-unknown-pawn", "Human_unknown_recovery_pawn")
	if err != nil {
		return err
	}
	if unknown["refused"] == nil {
		return fmt.Errorf("recovery-unknown-pawn: expected a refusal, got %#v", unknown)
	}

	repair, err := apply("recovery-repair", pawnID)
	if err != nil {
		return err
	}
	applied, ok := na.AsMap(repair["applied"])
	if !ok {
		return fmt.Errorf("recovery-repair: expected an applied result, got %#v", repair)
	}
	observed, _ := na.AsMap(applied["observed"])
	job, _ := na.AsMap(observed["job"])
	if na.AsString(job["pawnId"]) != pawnID || na.AsString(job["jobDef"]) != "Repair" {
		return fmt.Errorf("recovery-repair: unexpected applied repair evidence: %#v", job)
	}
	if targetA, _ := na.AsMap(job["targetA"]); na.AsString(targetA["thingId"]) != wallID {
		return fmt.Errorf("recovery-repair: unexpected repair target: %#v", job)
	}
	replay, err := apply("recovery-repair", pawnID)
	if err != nil {
		return err
	}
	if !na.DeepEqual(replay, repair) {
		return fmt.Errorf("recovery-repair: resent key changed its result: %#v then %#v", repair, replay)
	}

	// Run real game time until the wall is back to full HitPoints.
	for advanced := 0; ; advanced += 300 {
		if advanced >= 3*na.TicksPerDay {
			return fmt.Errorf("wall was not repaired within %d ticks", advanced)
		}
		if _, err := s.Advance(ctx, 300); err != nil {
			return err
		}
		afterHitPoints, afterMax, err := wallState(fmt.Sprintf("wall-%d", advanced))
		if err != nil {
			return err
		}
		if afterHitPoints >= afterMax {
			report["wall_after_hit_points"] = afterHitPoints
			break
		}
	}
	report["wall_repaired"] = true

	logData, err := os.ReadFile(s.Config().StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), s.Config().Headless)
}
