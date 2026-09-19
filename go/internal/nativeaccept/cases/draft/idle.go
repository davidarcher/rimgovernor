package draft

import (
	"context"
	"encoding/json"
	"fmt"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func init() {
	for _, hostile := range []bool{false, true} {
		name := "draft/idle"
		if hostile {
			name += "-hostile"
		}
		cases.Register(cases.Case{
			Name: name, Start: cases.Save{Name: "RimGovernor-tribal8-baseline"}, Budget: 3 * time.Minute,
			Scope:       "Manual player drafts are adopted and released within one Auto observation window; the same fixture with hostiles retains drafts for squad defense (#465).",
			RequiredOps: []string{"test/b04f_setup"},
			Serve:       &cases.ServeSpec{Families: []string{"defense"}, Prefix: "idle-draft", Extra: []string{"--clock-window-ticks", "300"}},
			Run:         func(ctx context.Context, s cases.Session) error { return runIdle(ctx, s, hostile) },
		})
	}
}

func runIdle(ctx context.Context, s cases.Session, hostile bool) error {
	h, identity := s.Harness(), s.Identity()
	grant, err := na.GrantAuto(ctx, h.WireFunc(), "fixture-auto", identity)
	if err != nil {
		return err
	}
	manual, err := na.RevokeManual(ctx, h.WireFunc(), "fixture-manual", identity, grant)
	if err != nil {
		return err
	}
	s.Report()["manual_before_player_draft"] = manual
	reply, err := h.Wire(ctx, "colonists", "observations_list_pawns", map[string]any{
		"scope": map[string]any{"expectedIdentity": identity}, "filter": map[string]any{"colonist": true},
		"details": map[string]any{"health": true, "biography": true, "equipment": true},
	})
	if err != nil {
		return err
	}
	_, observed, err := na.Outcome(reply, "observed")
	if err != nil {
		return err
	}
	var ids []string
	for _, raw := range na.AsSlice(observed["pawns"]) {
		row, _ := na.AsMap(raw)
		if row["dead"] != false || row["downed"] != false {
			continue
		}
		bio, _ := na.AsMap(row["biography"])
		blocked := false
		for _, tag := range na.AsSlice(bio["disabledWorkTags"]) {
			blocked = blocked || na.AsString(tag) == "Violent" || na.AsString(tag) == "Shooting"
		}
		if blocked || bio == nil {
			continue
		}
		pawn, _ := na.AsMap(row["pawn"])
		ids = append(ids, na.AsString(pawn["id"]))
	}
	if len(ids) < 4 {
		return fmt.Errorf("fixture needs four capable colonists, got %v", ids)
	}
	fixture := func(label, op, pawn string, drafted bool) error {
		out, err := h.Call(ctx, label, "test/b04f_setup", map[string]any{"op": op, "pawn": pawn, "drafted": drafted})
		s.Report()[label] = out
		if err != nil {
			return err
		}
		if out["success"] != true {
			return fmt.Errorf("%s refused: %v", label, out)
		}
		return nil
	}
	for _, id := range ids {
		if err = fixture("equip-"+id, "ranged-equipment", id, false); err != nil {
			return err
		}
		if err = fixture("idle-"+id, "idle-pawn", id, false); err != nil {
			return err
		}
		if err = fixture("player-draft-"+id, "external-draft", id, true); err != nil {
			return err
		}
		before, err := h.Wire(ctx, "draft-before-"+id, "observations_list_pawns", map[string]any{"scope": map[string]any{"expectedIdentity": identity}, "filter": map[string]any{"ids": []string{id}}})
		if err != nil {
			return err
		}
		row, err := na.PawnRow(before, identity, id)
		if err != nil {
			return err
		}
		if row["drafted"] != true || !na.DeepEqual(row["draftClaim"], map[string]any{"unowned": map[string]any{}}) {
			return fmt.Errorf("player draft not unclaimed: %v", row)
		}
	}
	if hostile {
		if err = fixture("hostiles", "ranged-opponents", ids[0], false); err != nil {
			return err
		}
	}
	// Tail before launch; evidence must be a native pawn read while Auto is
	// running, before Stop could cause any shutdown or lease cleanup.
	tail := na.NewFlightTail(na.FlightRecorderPath(s.Config().Output))
	service, err := s.Serve(ctx, s.Spec())
	if err != nil {
		return err
	}
	defer service.Stop()
	attached, err := service.Get("GET", "/api/state")
	if err != nil {
		return err
	}
	s.Report()["manual_before_auto"] = attached
	if na.AsString(attached["mode"]) == "automate" {
		return fmt.Errorf("service entered Auto before the requested transition")
	}
	if _, err = service.Acquire(); err != nil {
		return err
	}
	journal, err := service.Store(ctx)
	if err != nil {
		return err
	}
	started := time.Now()
	const window = 30 * time.Second // scheduler's full-observation safety net
	seen := map[string]map[string]any{}
	err = na.WaitProgress(ctx, na.Wait{Ceiling: window, Stall: window, Interval: 100 * time.Millisecond, Terminal: service.Exited}, func(ctx context.Context) (string, bool, error) {
		rows, err := tail.Next()
		if err != nil {
			return "", false, err
		}
		for _, event := range rows {
			if event.Kind != "native_response" || na.AsString(event.Payload["native_tool"]) != "rimgovernor/observations_list_pawns" {
				continue
			}
			result, _ := na.AsMap(event.Payload["result"])
			var reply map[string]any
			if json.Unmarshal([]byte(na.AsString(result["payload"])), &reply) != nil {
				continue
			}
			if _, err := na.PawnRow(reply, identity, ""); err != nil {
				continue
			}
			_, observed, _ := na.Outcome(reply, "observed")
			for _, raw := range na.AsSlice(observed["pawns"]) {
				row, _ := na.AsMap(raw)
				pawn, _ := na.AsMap(row["pawn"])
				id := na.AsString(pawn["id"])
				if !na.Contains(ids, id) {
					continue
				}
				snapshot, _ := na.AsMap(pawn["snapshot"])
				if na.AsString(snapshot["entityId"]) != id || na.AsString(snapshot["token"]) == "" || !na.DeepEqual(snapshot["context"], observed["context"]) {
					continue
				}
				claim, _ := na.AsMap(row["draftClaim"])
				_, unowned := claim["unowned"]
				_, owned := claim["owned"]
				if !hostile && row["drafted"] == false && unowned || hostile && row["drafted"] == true && owned {
					seen[id] = row
				}
			}
		}
		prefix := "routine-idle-draft-"
		if hostile {
			prefix = "routine-defense-"
		}
		plans, err := journal.PlanHistoryWithPrefix(ctx, prefix, 256)
		if err != nil {
			return "", false, err
		}
		adopted := false
		for _, plan := range plans {
			for _, progress := range plan.Progress {
				draft, ok := progress.Action().OwnedDraft()
				if !ok || seen[string(draft.Pawn())] == nil {
					continue
				}
				v := progress.View()
				cleanup, known := v.DraftCleanup.Value()
				if !known {
					continue
				}
				if !hostile && cleanup.Stage == domain.DraftReleased {
					adopted = true
				}
				if _, claimed := cleanup.Claim.Value(); hostile && claimed {
					adopted = true
				}
			}
		}
		if hostile {
			review, err := journal.LoadRoutineReview(ctx)
			if err != nil {
				return "", false, err
			}
			combat := false
			for _, binding := range review.Goals {
				goal, err := journal.LoadGoal(ctx, binding.Goal)
				if err != nil {
					return "", false, err
				}
				for _, method := range goal.Methods {
					if strings.HasPrefix(string(method.Method), "idle-draft-") {
						return "", false, fmt.Errorf("idle release admitted under hostile: %s", method.Plan)
					}
					combat = combat || binding.Need == policy.ActiveCombat && strings.HasPrefix(string(method.Method), "squad-")
				}
			}
			adopted = adopted && combat
		}
		return na.Signature(len(seen), adopted), adopted && (hostile || len(seen) == len(ids)), nil
	})
	s.Report()["native_pawns_after_auto"] = seen
	s.Report()["observation_window_ms"] = window.Milliseconds()
	s.Report()["elapsed_ms"] = time.Since(started).Milliseconds()
	return err
}
