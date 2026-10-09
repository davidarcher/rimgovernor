package wall

import (
	"context"
	"fmt"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"time"
)

func init() {
	cases.Register(cases.Case{Name: "wall/construction-skill", Start: cases.LabStart(), Crew: cases.Crew{Size: 3}, Budget: 4 * time.Minute,
		RequiredOps: []string{"test/construction_skill"},
		Scope:       "A native finishing-skill setting transfers from blueprint to frame and survives save/load; low-skill normal/prioritized finishing and direct completion are refused while a safe material-filled wall progresses through vanilla work. Equal-skilled completion succeeds and a same-cell replacement inherits nothing. The construction tier (#2522) placed with the floor survives conversion and save/load, set-tier re-tiers the frame, a stale target id is refused and the floor stays write-once. Native because Go snapshots cannot prove Harmony work-giver hooks, save references, conversion or pawn construction.", Run: runConstructionSkill})
}

func runConstructionSkill(ctx context.Context, s cases.Session) error {
	h := s.Harness()
	call := func(action string) (map[string]any, error) {
		row, err := h.Call(ctx, "construction-skill-"+action, "test/construction_skill", map[string]any{"action": action})
		if err != nil {
			return nil, err
		}
		if ok, _ := na.AsBool(row["success"]); !ok {
			return nil, fmt.Errorf("construction skill %s: %#v", action, row)
		}
		return row, nil
	}
	prepared, err := call("prepare")
	if err != nil {
		return err
	}
	id, err := na.ReadIdentity(ctx, h, "construction-skill-identity")
	if err != nil {
		return err
	}
	if _, err = na.GrantAuto(ctx, h.WireFunc(), "construction-skill-authority", id); err != nil {
		return err
	}
	placement := map[string]any{"defName": "Bed", "stuff": "WoodLog", "x": prepared["x"], "z": prepared["z"], "rotation": "ROTATION_NORTH"}
	building := func(label string, intent map[string]any) (map[string]any, error) {
		intent["placement"] = placement
		return na.ApplyOne(ctx, h, label, id, label, map[string]any{"building": intent})
	}
	if _, err = building("construction-minimum", map[string]any{"minimumFinishingSkill": 9, "tier": 3, "existingTargetId": prepared["target"]}); err != nil {
		return err
	}
	converted, err := call("convert")
	if err != nil {
		return err
	}
	if na.AsNumber(converted["minimum"]) != 9 || na.AsNumber(converted["tier"]) != 3 {
		return fmt.Errorf("conversion lost minimum or tier: %#v", converted)
	}
	// The blueprint id is stale once the frame stands; a different tier on the frame is accepted,
	// and the write-once finishing floor refuses a different minimum.
	stale, err := building("construction-tier-stale", map[string]any{"tier": 1, "existingTargetId": prepared["target"]})
	if err != nil {
		return err
	}
	if err = na.Refused("construction-tier-stale", stale, "stale"); err != nil {
		return err
	}
	retiered, err := building("construction-tier-set", map[string]any{"tier": 1, "existingTargetId": converted["target"]})
	if err != nil {
		return err
	}
	if _, ok := na.AsMap(retiered["applied"]); !ok {
		return fmt.Errorf("set-tier not applied: %#v", retiered)
	}
	floor, err := building("construction-floor-change", map[string]any{"minimumFinishingSkill": 10, "existingTargetId": converted["target"]})
	if err != nil {
		return err
	}
	if err = na.Refused("construction-floor-change", floor, "already set"); err != nil {
		return err
	}
	if audit, e := call("audit"); e != nil || na.AsNumber(audit["tier"]) != 1 || na.AsNumber(audit["minimum"]) != 9 {
		return fmt.Errorf("set-tier or floor readback: %#v %v", audit, e)
	}
	name := fmt.Sprintf("construction-skill-%d", time.Now().UnixNano())
	saved, err := h.Wire(ctx, "skill-save", "lifecycle_save", map[string]any{"player": map[string]any{"identity": id, "playerDirection": 1, "requestId": name}, "saveName": name})
	if err != nil {
		return err
	}
	if _, _, err = na.Outcome(saved, "completed"); err != nil {
		return err
	}
	loadID := name + "-load"
	loaded, err := h.Wire(ctx, "skill-load", "lifecycle_load", map[string]any{"requestId": loadID, "saveName": name, "readiness": "READINESS_MAP", "expectedPlayer": map[string]any{"identity": id, "playerDirection": 1, "requestId": loadID}, "playerDirection": 1})
	if err != nil {
		return err
	}
	if _, err = na.PollLoad(ctx, h, loaded, loadID, "skill-load-poll"); err != nil {
		return err
	}
	// RunUntil uses this harness's fresh identity, not the pre-load Session snapshot.
	_, err = na.RunUntil(ctx, h, "wall-progress", 6000, na.Wait{}, func(ctx context.Context) (string, bool, error) {
		row, e := call("audit")
		if e != nil {
			return "", false, e
		}
		if na.AsNumber(row["minimum"]) != 9 || na.AsNumber(row["tier"]) != 1 {
			return "", false, fmt.Errorf("save lost minimum or tier: %#v", row)
		}
		for _, key := range []string{"lowRefused", "highAllowed", "bedPending"} {
			if ok, _ := na.AsBool(row[key]); !ok {
				return "", false, fmt.Errorf("%s: %#v", key, row)
			}
		}
		built, _ := na.AsBool(row["wallProgress"])
		return "material-filled wall", built, nil
	})
	if err != nil {
		return err
	}
	controls, err := call("controls")
	if err != nil {
		return err
	}
	for _, key := range []string{"lowRefused", "lowCompletionRefused", "completed", "noLeak"} {
		if ok, _ := na.AsBool(controls[key]); !ok {
			return fmt.Errorf("%s: %#v", key, controls)
		}
	}
	s.Report()["construction_skill"] = controls
	return nil
}
