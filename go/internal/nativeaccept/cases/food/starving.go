package food

import (
	"context"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

const starvingPrepareOp, starvingObserveOp = "test/food_starving_prepare", "test/food_starving_observe"

// starvingRoundTicks is the game time between samples (half a day); starvingRounds
// samples cover three days, the span the epic (#2140) asks about.
const (
	starvingRoundTicks = 30000
	starvingRounds     = 6
)

func init() {
	cases.Register(cases.Case{Name: "food/starving-tribal",
		Scope: "Diagnosis (#2141), asserts only that the report was produced: a starving tribal colony (tribal8, no food, no weapons, no butcher bill, six wild deer) is sampled every half day for three days with the live foodPlan portfolio (kind, id, decision, reason, terms, DeliveredPerDay, gap, runway), " +
			"which hunt blockers hold (butcher bill, Cooking worker, ordinary ranged weapon, three gunners, pending-hunt cap), pawn weapons and work state, wildlife counts and the food channel census. " +
			"A Go snapshot test over recorded colony facts cannot cover it: the weapon, work and wildlife state only exist on a live map; the recorded facts it yields become the replay snapshot.",
		Start: EmptyChannels("MealSurvivalPack", 0), RequiredOps: []string{starvingPrepareOp, starvingObserveOp}, Keep: []string{"Food"},
		Service: true, Budget: 15 * time.Minute, Stall: 2 * time.Minute, Run: runStarvingTribal})
}

func runStarvingTribal(ctx context.Context, s cases.Session) error {
	report := s.Report()
	h := s.Harness()
	identity := s.Identity()
	prepared, _ := na.AsMap(cases.RestoredState(s, "fixture"))
	var err error
	if prepared == nil {
		if prepared, err = h.Call(ctx, "starving-prepare", starvingPrepareOp, nil); err != nil {
			return err
		}
		na.SetCheckpointState("fixture", prepared)
	}
	if ok, _ := na.AsBool(prepared["success"]); !ok || len(na.AsSlice(prepared["animals"])) == 0 {
		return fmt.Errorf("starving fixture refused: %v", prepared)
	}
	report["fixture"] = prepared
	if _, err = na.ConfirmColonyNames(ctx, h, report); err != nil {
		return err
	}
	service, err := s.Launch(ctx, na.ServiceLaunch{Families: []string{"acquisition", "field", "work", "bill", "research"}, Extra: na.ClockSpeedArgs()})
	if err != nil {
		return err
	}
	defer func() { service.Stop() }()
	samples := make([]any, 0, starvingRounds)
	for round := 1; round <= starvingRounds; round++ {
		prefix := fmt.Sprintf("starving-%d", round)
		if round > 1 {
			if _, e := na.ConfirmColonyNames(ctx, h, report); e != nil {
				return e
			}
			if e := s.Release(); e != nil {
				return e
			}
			service.Identity = identity
			if service, err = service.Restart(ctx); err != nil {
				return err
			}
		}
		token, e := service.SessionToken()
		if e != nil {
			return e
		}
		if _, e = service.WaitAttached(identity, 90*time.Second); e != nil {
			return e
		}
		if _, e = service.Resume(prefix, identity, token, report); e != nil {
			return e
		}
		keep := (&na.AuthorityKeepAlive{Service: service, Prefix: prefix, Identity: identity, Token: token}).Start(ctx)
		journal, e := na.OpenStoreWithRetry(ctx, service.StatePath)
		if e != nil {
			return e
		}
		// A round ends after starvingRoundTicks of game time, or when the
		// clock parks (no_work freezes the tick; the parked tick is itself a
		// finding, so the sample is taken and recorded as such).
		var start, lastTick domain.Tick
		var parkedSince time.Time
		parked := false
		e = na.WaitProgress(ctx, na.Wait{Stall: na.StallBudget(), Terminal: service.Exited}, func(ctx context.Context) (string, bool, error) {
			r, e := journal.LoadRounds(ctx)
			if e != nil {
				return "", false, e
			}
			if start == 0 {
				start = r.Tick
			}
			if r.Tick != lastTick {
				lastTick, parkedSince = r.Tick, time.Now()
			}
			parked = r.Revision > 0 && time.Since(parkedSince) > 20*time.Second
			return na.Signature(r.Tick), r.Tick >= start+starvingRoundTicks || parked, nil
		})
		var colony map[string]any
		if e == nil {
			colony, e = service.Get("GET", "/api/player/colony")
		}
		journal.Close()
		keep()
		service.Stop()
		if e != nil {
			return fmt.Errorf("%s: %w", prefix, e)
		}
		if h, e = s.Reattach(ctx); e != nil {
			return e
		}
		if e = pauseForProbe(ctx, h, prefix+"-probe"); e != nil {
			return e
		}
		observed, e := h.Call(ctx, prefix+"-observe", starvingObserveOp, nil)
		if e != nil {
			return e
		}
		sample := map[string]any{"round": round, "clockParked": parked, "native": observed,
			"foodPlan": colony["foodPlan"], "blockers": huntBlockers(observed)}
		if reply, e := h.Wire(ctx, prefix+"-facts", "observations_read_colony_facts", map[string]any{"scope": map[string]any{"expectedIdentity": identity}, "planning": true}); e == nil {
			if _, facts, e := na.Outcome(reply, "observed"); e == nil {
				sample["foodChannels"] = facts["foodChannels"]
				sample["foodNutrition"] = facts["foodNutrition"]
			}
		}
		plan, _ := na.AsMap(colony["foodPlan"])
		sample["huntRows"] = portfolioRows(plan, policy.FoodHunt)
		sample["openHuntRow"] = openRow(sample["huntRows"])
		samples = append(samples, sample)
		report["samples"] = samples
		report["latest"] = sample
	}
	if len(samples) != starvingRounds {
		return fmt.Errorf("starving report incomplete: %d of %d samples", len(samples), starvingRounds)
	}
	return nil
}

// portfolioRows returns the foodPlan portfolio rows of one channel kind.
func portfolioRows(plan map[string]any, kind policy.FoodChannelKind) []any {
	var rows []any
	for _, raw := range na.AsSlice(plan["portfolio"]) {
		if row, _ := na.AsMap(raw); na.AsString(row["kind"]) == string(kind) {
			rows = append(rows, row)
		}
	}
	return rows
}

func openRow(rows any) bool {
	for _, raw := range na.AsSlice(rows) {
		if row, _ := na.AsMap(raw); row["decision"] == "Open" {
			return true
		}
	}
	return false
}

// huntBlockers names which hunt gates hold in one observe read: the native
// Eligible rule's inputs (butcher bill, Cooking worker, ordinary ranged
// weapon, Hunting enabled), the squad's three-gunner floor and the
// two-hunt pending cap. Route safety shows in each animal's own reason.
func huntBlockers(observed map[string]any) map[string]any {
	gunners, hunters, cooks := 0, 0, 0
	for _, raw := range na.AsSlice(observed["colonists"]) {
		c, _ := na.AsMap(raw)
		if na.AsNumber(c["hunting"]) > 0 {
			hunters++
		}
		if na.AsNumber(c["cooking"]) > 0 {
			cooks++
		}
		if ranged, _ := na.AsBool(c["ranged"]); ranged {
			gunners++
		}
	}
	butcher := 0
	for _, raw := range na.AsSlice(observed["bills"]) {
		b, _ := na.AsMap(raw)
		if isButcher, _ := na.AsBool(b["butcher"]); isButcher {
			butcher++
		}
	}
	return map[string]any{
		"noButcherBill":      butcher == 0,
		"noCookingWorker":    cooks == 0,
		"noHuntingWorker":    hunters == 0,
		"noRangedWeapon":     gunners == 0,
		"underSquadFloor":    gunners < policy.SquadHuntMinGunners,
		"pendingHuntCapHit":  na.AsNumber(observed["pendingHunts"]) >= 2,
		"rangedPawns":        gunners,
		"squadHuntMinGunner": policy.SquadHuntMinGunners,
	}
}
