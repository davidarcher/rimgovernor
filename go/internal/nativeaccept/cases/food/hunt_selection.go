package food

import (
	"context"
	"encoding/json"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
)

func init() {
	cases.Register(cases.Case{Name: "food/hunt-selection", Scope: "Herd revenge cost selects the safe deer first; ordinary native hunting produces its corpse and the channel explains risk and work.", Start: EmptyChannels("MealSurvivalPack", 0), RequiredOps: []string{"test/hunt_selection_prepare"}, Budget: 3 * time.Minute, Run: huntSelection})
}

func huntSelection(ctx context.Context, s cases.Session) error {
	h := s.Harness()
	prepared, err := h.Call(ctx, "hunt-prepare", "test/hunt_selection_prepare", nil)
	if err != nil {
		return err
	}
	reply, err := h.Wire(ctx, "hunt-facts", "observations_read_colony_facts", map[string]any{"scope": map[string]any{"expectedIdentity": s.Identity()}, "planning": true})
	if err != nil {
		return err
	}
	raw, err := json.Marshal(reply)
	if err != nil {
		return err
	}
	facts := &o.ColonyFactsReply{}
	if err = protojson.Unmarshal(raw, facts); err != nil {
		return err
	}
	if facts.GetObserved() == nil {
		return fmt.Errorf("hunt census unavailable: %v", facts)
	}
	sources := observation.ColonyAcquisition(facts.GetObserved())
	rows, known := sources.Value()
	if !known {
		return fmt.Errorf("hunt acquisition unavailable")
	}
	herd := 0
	for _, row := range rows {
		if row.Definition == "Muffalo" && row.HerdSize == 3 && row.RevengeChance > 0 {
			herd++
		}
	}
	if herd != 3 {
		return fmt.Errorf("missing herd risk facts: %+v", rows)
	}
	selected, err := policy.SelectAcquisition(sources, domain.Known(0.1), domain.Known(0.0), true, nil, domain.Known(1))
	if err != nil {
		return err
	}
	if len(selected) != 1 || selected[0].ID != na.AsString(prepared["deer"]) {
		return fmt.Errorf("safe deer not selected first: %+v", selected)
	}
	channels := policy.HuntChannels(rows)
	s.Report()["hunt_channels"] = channels
	s.Report()["selected"] = selected
	_, err = na.GrantAuto(ctx, h.WireFunc(), "hunt-grant", s.Identity())
	if err != nil {
		return err
	}
	_, generation, err := na.AuthorityStatus(ctx, h.WireFunc(), "hunt-authority", s.Identity())
	if err != nil {
		return err
	}
	attempt := map[string]any{"controllerSessionId": "hunt-selection", "actionId": "hunt", "attemptId": "1"}
	prey := selected[0]
	result, err := h.Wire(ctx, "hunt-designate", "operations_execute", map[string]any{
		"precondition": map[string]any{"identity": s.Identity(), "expectedGeneration": fmt.Sprint(generation), "attempt": attempt},
		"operation":    map[string]any{"acquireResource": map[string]any{"source": map[string]any{"entityId": prey.ID}, "resourceDefName": prey.Resource, "cell": map[string]any{"x": prey.Cell.X, "z": prey.Cell.Z}}},
	})
	if err != nil {
		return err
	}
	_, receipt, err := na.Outcome(result, "receipt")
	if err != nil {
		return err
	}
	if _, ok := na.AsMap(receipt["applied"]); !ok {
		return fmt.Errorf("hunt refused: %v", receipt)
	}
	for ticks := 0; ticks < 15000; ticks += 250 {
		if _, err = s.Advance(ctx, 250); err != nil {
			return err
		}
		progress, err := h.Wire(ctx, "hunt-progress", "receipts_observe_progress", map[string]any{"identity": s.Identity(), "attempt": attempt})
		if err != nil {
			return err
		}
		_, observed, err := na.Outcome(progress, "progress")
		if err != nil {
			return err
		}
		if complete, ok := na.AsMap(observed["completed"]); ok {
			s.Report()["native_completed"] = complete
			return nil
		}
	}
	return fmt.Errorf("selected deer did not produce a native completed acquisition within 15000 ticks")
}
