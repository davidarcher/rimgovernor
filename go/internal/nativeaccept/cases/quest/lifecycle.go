package quest

import (
	"context"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"time"
)

func lifecycleQuest(ctx context.Context, h *na.Harness, identity map[string]any, id string) (map[string]any, error) {
	reply, err := h.Wire(ctx, "quest-lifecycle-census", "observations_read_world_progression", map[string]any{"scope": map[string]any{"expectedIdentity": identity}, "includeStorage": false})
	if err != nil {
		return nil, err
	}
	_, observed, err := na.Outcome(reply, "observed")
	if err != nil {
		return nil, err
	}
	for _, raw := range na.AsSlice(observed["quests"]) {
		row, _ := na.AsMap(raw)
		if na.AsString(row["id"]) == id {
			return row, nil
		}
	}
	return nil, fmt.Errorf("quest %s absent from native world progression", id)
}

func waitLifecycleOrders(ctx context.Context, service *na.ServiceProcess, st *store.Store, ready func([]store.PlanState) bool) error {
	tick := func(context.Context) (uint64, error) {
		row, err := service.Get("GET", "/api/player/colony")
		return uint64(na.AsNumber(row["tick"])), err
	}
	return na.WaitProgress(ctx, na.Wait{Ceiling: 8 * time.Minute, Stall: na.StallBudget(), Terminal: service.Exited, Ticks: 30000, Tick: tick}, func(ctx context.Context) (string, bool, error) {
		plans, err := st.PlanHistoryWithMethods(ctx, 256, "quest-*", "decree-*")
		if err != nil {
			return "", false, err
		}
		signature := make([]string, 0, len(plans))
		for _, p := range plans {
			for _, step := range p.Progress {
				signature = append(signature, fmt.Sprintf("%s:%s", p.Method, step.View().Stage))
			}
		}
		observedTick, err := tick(ctx)
		if err != nil {
			return "", false, err
		}
		return na.Signature(signature, observedTick), ready(plans), nil
	})
}

func completedLifecycleActions(plans []store.PlanState, check func(domain.Action)) {
	for _, p := range plans {
		for _, step := range p.Progress {
			if step.View().Stage == domain.Completed {
				check(step.Action())
			}
		}
	}
}

func finishLifecycle(ctx context.Context, s cases.Session, ids ...string) error {
	h, err := s.Reattach(ctx)
	if err != nil {
		return err
	}
	elapsed, err := na.RunUntil(ctx, h, "quest-native-completion", 30000, na.Wait{Stall: na.StallBudget()}, func(ctx context.Context) (string, bool, error) {
		states := make([]string, 0, len(ids))
		done := true
		for _, id := range ids {
			row, err := lifecycleQuest(ctx, h, s.Identity(), id)
			if err != nil {
				return "", false, err
			}
			state := na.AsString(row["state"])
			states = append(states, state)
			if state == "QUEST_STATUS_ENDED_FAILED" || state == "QUEST_STATUS_ENDED_INVALID" || state == "QUEST_STATUS_ENDED_OFFER_EXPIRED" {
				return "", false, fmt.Errorf("quest %s ended %s", id, state)
			}
			done = done && state == "QUEST_STATUS_ENDED_SUCCESS"
			s.Report()["quest_"+id] = row
		}
		return na.Signature(states), done, nil
	})
	s.Report()["completion_ticks"] = elapsed
	return err
}
