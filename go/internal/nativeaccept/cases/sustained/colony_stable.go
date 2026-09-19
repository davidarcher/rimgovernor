package sustained

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/sustainedfood"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
)

// Fixed equivalent of RIMGOVERNOR_ACCEPT_WINDOW_TICKS=900000: a shorter
// diagnostic environment override must not shorten the nightly gate.
const colonyStableTicks uint64 = 900000

func init() {
	c := colony("sustained/colony-stable", na.QuietRequired,
		"Fifteen game days on tribal8 with every routine family and live needs: no colonist lost or dead, no Malnutrition above 0.3, every immunity disease immune or tended, and food days at least FoodTargetDays.")
	// At 100 ticks/s the window takes 2.5 hours. The service still enforces
	// its 90-second step stall bound; this ceiling allows the full calendar.
	const watch = 3 * time.Hour
	c.Budget = watch + 7*time.Minute
	c.Reason = "nightly fifteen-day colony stability gate with live needs and every routine family"
	c.Run = func(ctx context.Context, s cases.Session) error {
		var initial []string
		_, err := sustainedfood.Observe(ctx, s, sustainedfood.Observation{
			WatchConfig: sustainedfood.WatchConfig{
				Watch: watch, Window: colonyStableTicks, Poll: 10 * time.Second,
				Goal: policy.EnsureFoodSupply, Extra: colonyGoals,
				FailFast: sustainedfood.FailFast{Disabled: true},
			},
			Prepare: func(ctx context.Context, h *na.Harness, report na.Report) error {
				listed, err := h.Call(ctx, "stable-initial", "home/list_pawns", map[string]any{"colonistsOnly": true})
				if err != nil {
					return err
				}
				for _, raw := range na.AsSlice(listed["pawns"]) {
					row, _ := na.AsMap(raw)
					id := na.AsString(row["thingId"])
					if id == "" {
						return fmt.Errorf("initial colonist has no thingId")
					}
					initial = append(initial, id)
				}
				report["initial_colonists"] = initial
				if len(initial) == 0 {
					return fmt.Errorf("no initial colonists observed")
				}
				return nil
			},
			Audit: func(ctx context.Context, h *na.Harness, report na.Report) error {
				var failures []error
				window, ok := report["window"].(*sustainedfood.TickWindow)
				if !ok || !window.Reached {
					failures = append(failures, fmt.Errorf("fifteen-day window not reached: %v", report["window"]))
				}
				failures = append(failures, auditReacquisitions(ctx, h, report), auditNutrition(ctx, h, report))
				listed, err := h.Call(ctx, "stable-health", "home/list_pawns", map[string]any{"colonistsOnly": true, "includeDead": true, "health": true})
				if err != nil {
					return errors.Join(append(failures, err)...)
				}
				report["stable_health"] = listed
				alive := map[string]bool{}
				for _, raw := range na.AsSlice(listed["pawns"]) {
					row, _ := na.AsMap(raw)
					id := na.AsString(row["thingId"])
					dead, known := na.AsBool(row["dead"])
					alive[id] = known && !dead
					if !alive[id] {
						failures = append(failures, fmt.Errorf("colonist %s dead or death status unknown", id))
					}
					health, ok := na.AsMap(row["health"])
					if !ok {
						failures = append(failures, fmt.Errorf("colonist %s health missing", id))
						continue
					}
					for _, raw := range na.AsSlice(health["hediffs"]) {
						disease, _ := na.AsMap(raw)
						immunizable, _ := na.AsBool(disease["immunizable"])
						immune, _ := na.AsBool(disease["fullyImmune"])
						tended, _ := na.AsBool(disease["isTended"])
						if immunizable && !immune && !tended {
							failures = append(failures, fmt.Errorf("colonist %s has untreated nonimmune %s", id, na.AsString(disease["defName"])))
						}
					}
				}
				// Corpses can despawn or be buried: a missing initial pawn must
				// fail even when list_pawns no longer returns the dead pawn.
				for _, id := range initial {
					if !alive[id] {
						failures = append(failures, fmt.Errorf("initial colonist %s no longer alive on map", id))
					}
				}
				failures = append(failures, auditStableFood(ctx, h, s, report))
				return errors.Join(failures...)
			},
		})
		return err
	}
	cases.Register(c)
}

func auditStableFood(ctx context.Context, h *na.Harness, s cases.Session, report na.Report) error {
	facts, err := h.Wire(ctx, "stable-food", "observations_read_colony_facts", map[string]any{"scope": map[string]any{"expectedIdentity": s.Identity()}})
	if err != nil {
		return err
	}
	raw, err := json.Marshal(facts)
	if err != nil {
		return err
	}
	reply := &o.ColonyFactsReply{}
	if err := protojson.Unmarshal(raw, reply); err != nil {
		return err
	}
	v := reply.GetObserved()
	if v == nil || v.Context == nil || v.Context.Identity == nil {
		return fmt.Errorf("final colony facts unavailable")
	}
	id := v.Context.Identity
	projection, err := observation.DecodeColony(reply, observation.Identity{Colony: domain.ColonyID(id.GetColonyId()), Map: domain.MapID(id.GetMapId()), Load: domain.LoadID(id.GetLoadToken()), Tick: domain.Tick(v.Context.GetTick())})
	if err != nil {
		return err
	}
	// Use the controller's combined diet/rot/animal-feed forecast and
	// seasonal target, not the census's simple nutrition/demand ratio.
	days, known := projection.Facts.FoodDays.Value()
	target := policy.DefaultRoutinePolicy().Seasonal(projection.Facts.Calendar, projection.Facts.DisasterConditions).FoodTargetDays
	report["stable_food"] = map[string]any{"food_days": days, "food_target_days": target}
	if !known || days < target {
		return fmt.Errorf("final food days %v (known=%v) below FoodTargetDays %v", days, known, target)
	}
	return nil
}
