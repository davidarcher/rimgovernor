package mood

import (
	"context"
	"encoding/json"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func init() {
	cases.Register(cases.Case{
		Name:        "mood/berserk",
		Scope:       "Tribal8 controller-selected melee SUBDUE then RESCUE to the pawn's own bed; no deaths, prisoner conversion or forced recovery, with other dispatch outside containment.",
		Start:       cases.Fixture{On: cases.Save{Name: "RimGovernor-tribal8-baseline"}, Op: "test/berserk_prepare"},
		RequiredOps: []string{"test/berserk_audit"}, QuietWorld: true,
		Serve:  &cases.ServeSpec{Families: []string{"defense", "rescue", "repair"}, Prefix: "berserk", Extra: []string{"--clock-window-ticks", "120"}},
		Budget: 4 * time.Minute, Run: runBerserk,
	})
}

func runBerserk(ctx context.Context, s cases.Session) error {
	fixture := s.Prepared()
	target, bed := na.AsString(fixture["target"]), na.AsString(fixture["bed"])
	squad := map[string]bool{}
	for _, id := range na.AsSlice(fixture["squad"]) {
		squad[na.AsString(id)] = true
	}
	if target == "" || bed == "" || len(squad) != 2 || len(na.AsSlice(fixture["colonists"])) != 8 {
		return fmt.Errorf("incomplete tribal8 berserk fixture: %v", fixture)
	}
	s.Report()["fixture"] = fixture
	audit := berserkDispatch{target: target, squad: squad, positions: map[string]domain.Cell{}}
	audit.positions[na.AsString(fixture["repair"])] = domain.Cell{X: int32(na.AsNumber(fixture["repairX"])), Z: int32(na.AsNumber(fixture["repairZ"]))}
	before, err := s.Harness().Wire(ctx, "berserk-before", "observations_list_pawns", map[string]any{"scope": map[string]any{"expectedIdentity": s.Identity()}, "filter": map[string]any{"ids": fixture["colonists"], "includeDead": true}})
	if err != nil {
		return err
	}
	audit.observe(before)
	if !audit.active {
		return fmt.Errorf("fixture did not expose standing Berserk: %v", before)
	}
	tail := na.NewFlightTail(na.FlightRecorderPath(s.Config().Output))
	service, err := s.Serve(ctx, s.Spec())
	if err != nil {
		return err
	}
	defer service.Stop()
	if _, err = service.Acquire(); err != nil {
		return err
	}
	service.KeepAuthority(ctx)
	journal, err := service.Store(ctx)
	if err != nil {
		return err
	}
	selected := map[string]bool{}
	subdue, rescue := false, false
	err = na.WaitProgress(ctx, na.Wait{Ceiling: 3 * time.Minute, Stall: 60 * time.Second, Interval: 250 * time.Millisecond, Terminal: service.Exited}, func(ctx context.Context) (string, bool, error) {
		events, err := tail.Next()
		if err != nil {
			return "", false, err
		}
		for _, event := range events {
			if err := audit.observeEvent(event); err != nil {
				return "", false, err
			}
			operation, dispatched, err := dispatchedOperation(event)
			if err != nil {
				return "", false, err
			}
			if dispatched {
				if err := audit.dispatch(operation); err != nil {
					return "", false, err
				}
			}
		}
		plans, err := journal.PlanHistoryWithPrefix(ctx, "routine-", 256)
		if err != nil {
			return "", false, err
		}
		var stages []string
		for _, plan := range plans {
			for i, action := range plan.Spec.Actions() {
				v := plan.Progress[i].View()
				if m, ok := action.MeleeAttack(); ok && m.Subdue() && string(m.Target()) == target {
					if !squad[string(m.Pawn())] {
						return "", false, fmt.Errorf("controller selected non-melee fixture responder %s", m.Pawn())
					}
					selected[string(m.Pawn())] = true
					stages = append(stages, string(v.Stage))
					subdue = subdue || v.Stage == domain.Completed
				}
				if r, ok := action.Rescue(); ok && string(r.Patient()) == target {
					stages = append(stages, string(v.Stage))
					rescue = rescue || v.Stage == domain.Completed
				}
			}
		}
		return na.Signature(stages, audit.subdues, audit.rescues, audit.active), subdue && rescue, nil
	})
	s.Report()["dispatch_audit"] = map[string]any{"subdue_requests": audit.subdues, "rescue_requests": audit.rescues, "containment_reads": audit.reads, "other_dispatch_checked": audit.checked, "selected": selected}
	service.Stop()
	if err != nil {
		return err
	}
	if len(selected) != 2 || audit.subdues == 0 || audit.rescues == 0 || audit.reads == 0 {
		return fmt.Errorf("missing squad/subdue/rescue/containment evidence: %v", s.Report()["dispatch_audit"])
	}
	h, err := s.Reattach(ctx)
	if err != nil {
		return err
	}
	after, err := h.Call(ctx, "berserk-outcome", "test/berserk_audit", map[string]any{})
	if err != nil {
		return err
	}
	s.Report()["outcome"] = after
	return berserkOutcome(after, bed)
}

func berserkOutcome(a map[string]any, bed string) error {
	if _, ok := a["deaths"].([]any); !ok {
		return fmt.Errorf("missing colonist death history: %v", a)
	}
	if _, ok := a["prisoners"].([]any); !ok {
		return fmt.Errorf("missing prisoner history: %v", a)
	}
	if na.AsNumber(a["colonists"]) != 8 || a["alive"] != true || a["colonist"] != true || a["mental"] != false || a["downed"] != true || a["delivered"] != true ||
		len(na.AsSlice(a["deaths"])) != 0 || len(na.AsSlice(a["prisoners"])) != 0 || na.AsString(a["bed"]) != bed || na.AsString(a["ownedBed"]) != bed {
		return fmt.Errorf("berserk containment did not deliver a living non-prisoner colonist to their own bed: %v", a)
	}
	return nil
}

// Audit ordered wire requests against the last native pawn census, including
// dispatch between journal polls. Receipts alone cannot prove clearance.
type berserkDispatch struct {
	target                           string
	squad                            map[string]bool
	positions                        map[string]domain.Cell
	active                           bool
	reads, subdues, rescues, checked int
}

func (a *berserkDispatch) observe(reply map[string]any) {
	observed, _ := na.AsMap(reply["observed"])
	for _, raw := range na.AsSlice(observed["pawns"]) {
		row, _ := na.AsMap(raw)
		pawn, _ := na.AsMap(row["pawn"])
		id := na.AsString(pawn["id"])
		if pos, ok := na.AsMap(pawn["position"]); ok {
			a.positions[id] = domain.Cell{X: int32(na.AsNumber(pos["x"])), Z: int32(na.AsNumber(pos["z"]))}
		}
		if id == a.target {
			a.active = row["mentalStateIsAggro"] == true && row["downed"] == false && row["dead"] == false
			if a.active {
				a.reads++
			}
		}
	}
}

func (a *berserkDispatch) dispatch(op map[string]any) error {
	if _, ok := op["arrest"]; ok {
		return fmt.Errorf("arrest dispatched during subdue/rescue case")
	}
	if order, ok := na.AsMap(op["pawnTargetOrder"]); ok {
		pawn, _ := na.AsMap(order["pawn"])
		target, _ := na.AsMap(order["target"])
		kind := na.AsString(order["kind"])
		if kind == "PAWN_ORDER_KIND_CAPTURE" {
			return fmt.Errorf("capture dispatched during containment")
		}
		if kind == "PAWN_ORDER_KIND_SUBDUE" {
			if !a.squad[na.AsString(pawn["entityId"])] || na.AsString(target["entityId"]) != a.target {
				return fmt.Errorf("unexpected subdue squad/target: %v", order)
			}
			a.subdues++
			return nil
		}
		if kind == "PAWN_ORDER_KIND_RESCUE" && na.AsString(target["entityId"]) == a.target {
			a.rescues++
		}
	}
	if draft, ok := na.AsMap(op["setDrafted"]); ok {
		pawn, _ := na.AsMap(draft["pawn"])
		if draft["drafted"] == false || a.squad[na.AsString(pawn["entityId"])] {
			return nil
		}
	}
	if !a.active {
		return nil
	}
	at, ok := a.positions[a.target]
	if !ok {
		return fmt.Errorf("containment target position missing")
	}
	located := false
	var visit func(any) error
	visit = func(value any) error {
		switch v := value.(type) {
		case map[string]any:
			if id := na.AsString(v["entityId"]); id != "" {
				if pos, ok := a.positions[id]; ok {
					located = true
					if policy.InBreakRadius(pos, at) {
						return fmt.Errorf("other dispatch entered containment at %s: %v", id, op)
					}
				} else {
					return fmt.Errorf("dispatch entity %s has no observed clearance: %v", id, op)
				}
			}
			if x, xok := v["x"]; xok {
				if z, zok := v["z"]; zok {
					located = true
					if policy.InBreakRadius(domain.Cell{X: int32(na.AsNumber(x)), Z: int32(na.AsNumber(z))}, at) {
						return fmt.Errorf("other dispatch targeted containment: %v", op)
					}
				}
			}
			for _, child := range v {
				if err := visit(child); err != nil {
					return err
				}
			}
		case []any:
			for _, child := range v {
				if err := visit(child); err != nil {
					return err
				}
			}
		}
		return nil
	}
	if err := visit(op); err != nil {
		return err
	}
	if !located {
		return fmt.Errorf("cannot establish clearance of other dispatch: %v", op)
	}
	a.checked++
	return nil
}

func (a *berserkDispatch) observeEvent(event na.FlightRow) error {
	// Discovery responses name the native tool too, but contain schemas.
	if event.Kind != "native_response" || na.AsString(event.Payload["tool"]) != "games_call_tool" || na.AsString(event.Payload["native_tool"]) != "rimgovernor/observations_list_pawns" {
		return nil
	}
	result, _ := na.AsMap(event.Payload["result"])
	var reply map[string]any
	if err := json.Unmarshal([]byte(na.AsString(result["payload"])), &reply); err != nil {
		return err
	}
	a.observe(reply)
	return nil
}

// dispatchedOperation is the operation a flight row dispatched, when the row
// is one: a native_request through games_call_tool whose inner tool is
// operations_execute.
//
// The wrapper is part of the match, not only the tool the arguments name. A
// games_tool_detail row names the very same native tool while carrying no
// inner request at all, so matching on the inner tool alone unmarshalled an
// empty string and failed the case with a bare "unexpected end of JSON
// input" the first time the session described operations_execute -- once per
// fresh session, which is every nightly run (#663).
func dispatchedOperation(event na.FlightRow) (map[string]any, bool, error) {
	if event.Kind != "native_request" || na.AsString(event.Payload["tool"]) != "games_call_tool" {
		return nil, false, nil
	}
	args, _ := na.AsMap(event.Payload["arguments"])
	if na.AsString(args["tool"]) != "rimgovernor/operations_execute" {
		return nil, false, nil
	}
	inner, _ := na.AsMap(args["arguments"])
	var request map[string]any
	if err := json.Unmarshal([]byte(na.AsString(inner["request"])), &request); err != nil {
		return nil, false, fmt.Errorf("operations_execute request of flight row %d: %w", event.Sequence, err)
	}
	operation, _ := na.AsMap(request["operation"])
	return operation, true, nil
}
