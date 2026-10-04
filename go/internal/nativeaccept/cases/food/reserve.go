package food

import (
	"context"
	"fmt"
	"math"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

const reservePrepareOp, reserveProbeOp = "test/food_reserve_prepare", "test/food_reserve_probe"

// reserveSeedShare is the share of the default reserve target the fixture
// seeds as unforbidden pemmican in the food room (0.9: the bill has less to cook, so the fill finishes in fewer rounds): the stock stays short of
// the target, so the goal must both hold it and refill the rest with a
// preserve bill.
const reserveSeedShare = 0.9

// reserveDays is the reserve the service runs with at every stage.
const reserveDays = policy.DefaultFoodReserveDays

// reserveTargetUnits is the pemmican count the reserve asks for:
// reserveDays of 1.6 nutrition per colonist at 0.05 per unit.
func reserveTargetUnits(colonists int) int {
	return int(math.Ceil(reserveDays * float64(colonists) * 1.6 / 0.05))
}

// reserveRoundTicks is the game time one service round runs before the
// case stops it to read native state: the bill, the holds and the meals
// all move within a quarter day.
const reserveRoundTicks = 15000

func init() {
	cases.Register(cases.Case{Name: "food/reserve", Scope: "MaintainFoodStorage holds unforbidden pemmican and fills the rest with a target-count pemmican bill that stops at the reserve while simple meals feed the colony (#428); deleting every other food releases the reserve and the colonists eat it.",
		Start: EmptyChannels("MealSimple", 200), RequiredOps: []string{reservePrepareOp, reserveProbeOp}, Keep: []string{"Food"},
		Service: true, Budget: 12 * time.Minute, Stall: 2 * time.Minute, Run: runFoodReserve})
}

func runFoodReserve(ctx context.Context, s cases.Session) error {
	report := s.Report()
	h := s.Harness()
	identity := s.Identity()
	// A resume from the ring (#249) reloads a mid-run save that already
	// holds the food room and the seeded runway: preparing again would
	// stack a second runway on it and push the upkeep items census over
	// its bound (#1373), so the resumed run reads the fresh run's result.
	prepared, _ := na.AsMap(cases.RestoredState(s, "fixture"))
	var err error
	if prepared == nil {
		if prepared, err = h.Call(ctx, "reserve-prepare", reservePrepareOp, map[string]any{"reserveDays": reserveDays, "seedShare": reserveSeedShare}); err != nil {
			return err
		}
		na.SetCheckpointState("fixture", prepared)
	}
	seeded, _ := prepared["pemmican"].(float64)
	colonists, _ := prepared["colonists"].(float64)
	expected := reserveTargetUnits(int(colonists))
	if colonists < 1 || seeded < 1 || int(seeded) >= expected {
		return fmt.Errorf("fixture must seed pemmican short of the %d-unit reserve target: %v", expected, prepared)
	}
	report["expected_target_units"] = expected
	report["fixture"] = prepared
	if _, err = na.ConfirmColonyNames(ctx, h, report); err != nil {
		return err
	}
	// The cooking family stays out: its meal bill sits above the reserve bill
	// on the single stove and, with meals eaten as fast as they are cooked,
	// never lets the cook reach the pemmican.
	service, err := s.Launch(ctx, na.ServiceLaunch{Families: []string{"bill", "food-storage-upkeep"}, Extra: na.ClockSpeedArgs()})
	if err != nil {
		return err
	}
	defer func() { service.Stop() }()
	// round runs the service for reserveRoundTicks of game time (and until
	// no reviewed hold or release is left pending), stops it and reads the
	// native state through the probe. The durable review persists only the
	// pending reserve identities, so the native probe is the assertion.
	first := true
	round := func(prefix string, args map[string]any) (map[string]any, error) {
		if !first {
			// Days into the fill RimWorld opens its colony-naming dialog,
			// which parks the clock for good; the case's families leave it
			// to the harness, so answer it before the next round.
			if _, e := na.ConfirmColonyNames(ctx, h, report); e != nil {
				return nil, e
			}
			if e := s.Release(); e != nil {
				return nil, e
			}
			service.Identity = identity
			if service, err = service.Restart(ctx); err != nil {
				return nil, err
			}
		}
		first = false
		token, e := service.SessionToken()
		if e != nil {
			return nil, e
		}
		if _, e = service.WaitAttached(identity, 90*time.Second); e != nil {
			return nil, e
		}
		if _, e = service.Resume(prefix, identity, token, report); e != nil {
			return nil, e
		}
		keep := (&na.AuthorityKeepAlive{Service: service, Prefix: prefix, Identity: identity, Token: token}).Start(ctx)
		journal, e := na.OpenStoreWithRetry(ctx, service.StatePath)
		if e != nil {
			return nil, e
		}
		// A round ends once nothing is pending and either the round's game
		// time elapsed or the clock parked (no_work: the planners have nothing
		// left, so the tick stops moving); the review keeps revising at a
		// parked tick, so the tick alone is the stall signature.
		var start, lastTick domain.Tick
		var parkedSince time.Time
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
			parked := r.Revision > 0 && time.Since(parkedSince) > 20*time.Second
			bound := ""
			for _, g := range r.Goals {
				if g.Need == policy.MaintainFoodStorage {
					bound = string(g.Goal)
				}
			}
			return na.Signature(r.Tick, len(r.ReserveSupplies), bound), len(r.ReserveSupplies) == 0 && (r.Tick >= start+reserveRoundTicks || parked), nil
		})
		journal.Close()
		keep()
		service.Stop()
		if e != nil {
			return nil, fmt.Errorf("%s: %w", prefix, e)
		}
		if h, e = s.Reattach(ctx); e != nil {
			return nil, e
		}
		if e = pauseForProbe(ctx, h, prefix+"-probe"); e != nil {
			return nil, e
		}
		probe, e := h.Call(ctx, prefix+"-probe", reserveProbeOp, args)
		if e != nil {
			return nil, e
		}
		report[prefix] = probe
		return probe, nil
	}
	// Fill: the seeded pemmican is held, the bill tops the stock up to its
	// target and stacks are held until the target is covered; the bill then
	// pauses. Whole stacks are held, so the last batch may leave a free
	// surplus. The held count never falls: no colonist eats the reserve.
	var probe map[string]any
	lastHeld := 0
	for attempt := 1; ; attempt++ {
		if probe, err = round(fmt.Sprintf("fill-%d", attempt), nil); err != nil {
			return err
		}
		held, free, e := pemmicanUnits(probe)
		if e != nil {
			return e
		}
		if held < lastHeld {
			return fmt.Errorf("held pemmican fell from %d to %d units while other food existed: %v", lastHeld, held, probe)
		}
		lastHeld = held
		bill, e := reserveBill(probe)
		if e == nil {
			target, _ := bill["target"].(float64)
			paused, _ := na.AsBool(bill["paused"])
			if bill["repeat"] == "TargetCount" && paused && held >= int(target) && held >= int(seeded) && target > seeded {
				break
			}
		}
		if attempt == 8 {
			return fmt.Errorf("reserve never filled and held: %d held, %d free, bill %v: %v", held, free, bill, e)
		}
	}
	if other, _ := probe["otherFood"].(float64); other <= 0 {
		return fmt.Errorf("simple meals vanished before the drain: %v", probe)
	}
	// Drain: every other food goes and the colonists are hungry. The
	// runway falls under the seasonal minimum, the review releases every
	// held stack and the colonists eat it.
	args := map[string]any{"drain": true}
	for attempt := 1; ; attempt++ {
		if probe, err = round(fmt.Sprintf("release-%d", attempt), args); err != nil {
			return err
		}
		args = nil
		held, _, e := pemmicanUnits(probe)
		if e != nil {
			return e
		}
		// Released: eating needs game time the parked service clock no longer
		// gives, so the harness ticks the game itself before re-reading.
		for tick := 0; held == 0 && tick < 4; tick++ {
			if eaten, _ := probe["reserveEaten"].(float64); eaten > 0 {
				return nil
			}
			if _, err = s.Advance(ctx, 2500); err != nil {
				return err
			}
			if probe, err = h.Call(ctx, fmt.Sprintf("release-%d-eat-%d", attempt, tick), reserveProbeOp, nil); err != nil {
				return err
			}
			report[fmt.Sprintf("release-%d-eat", attempt)] = probe
		}
		if eaten, _ := probe["reserveEaten"].(float64); held == 0 && eaten > 0 {
			return nil
		}
		if attempt == 4 {
			return fmt.Errorf("reserve not released and eaten after the drain: %d held: %v", held, probe)
		}
	}
}

// pemmicanUnits sums the probe's pemmican stacks into forbidden (held)
// and unforbidden units.
func pemmicanUnits(probe map[string]any) (held, free int, err error) {
	rows := na.AsSlice(probe["pemmican"])
	if rows == nil {
		return 0, 0, fmt.Errorf("pemmican stacks unobserved: %v", probe)
	}
	for _, raw := range rows {
		row, _ := na.AsMap(raw)
		units, _ := row["units"].(float64)
		if forbidden, _ := na.AsBool(row["forbidden"]); forbidden {
			held += int(units)
		} else {
			free += int(units)
		}
	}
	return held, free, nil
}

func reserveBill(probe map[string]any) (map[string]any, error) {
	for _, raw := range na.AsSlice(probe["bills"]) {
		row, _ := na.AsMap(raw)
		if recipe := na.AsString(row["recipe"]); recipe == "Make_PemmicanBulk" || recipe == "Make_Pemmican" {
			return row, nil
		}
	}
	return nil, fmt.Errorf("native Make_Pemmican(Bulk) bill missing: %v", probe["bills"])
}
