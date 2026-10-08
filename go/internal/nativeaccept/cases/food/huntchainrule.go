package food

import (
	"context"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/routinefamily"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/planstage"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

const (
	chainPrepareOp = "test/hunt_chain_prepare"
	chainObserveOp = "test/hunt_chain_observe"
	chainID        = policy.HuntChainRuleID
	chainLease     = 2500
)

func init() {
	cases.Register(cases.Case{Name: "food/hunt-chain-rule",
		Scope: "Native postcondition and end-to-end signal (a Go snapshot test cannot cover a native rule firing on the kill hook): " +
			"one hunter and four designated deer; an out-of-whitelist rule is refused; the controller (service family rules, #2154) attaches the hunt-chain " +
			"rule itself through the journaled rules_attach action, and with it attached the hunter's job two ticks after the first kill is Hunt on another " +
			"live designated deer, not the vanilla haul of its own kill; with rules cleared the next kill " +
			"leaves vanilla behaviour; after the lease expires nothing fires.",
		Start:       cases.Fixture{Op: chainPrepareOp, On: cases.LabStart()},
		Serve:       &cases.ServeSpec{Families: []routinefamily.Family{routinefamily.Acquisition, routinefamily.Work, routinefamily.Rules}, NativeTimeout: 60 * time.Second, Prefix: "hunt-chain"},
		QuietWorld:  true,
		RequiredOps: []string{chainObserveOp}, Budget: 6 * time.Minute, Crew: cases.Crew{Size: 3},
		Reason: "a lab with one ranger, a corpse stockpile and four deer; one controller phase until the rules_attach receipt, then three native kills",
		Run:    runHuntChain})
}

func runHuntChain(ctx context.Context, s cases.Session) error {
	h := s.Harness()
	identity := s.Identity()
	for _, tool := range []string{"rimgovernor/rules_attach", "rimgovernor/rules_clear", "rimgovernor/rules_read_status"} {
		if !na.Contains(s.Names(), tool) {
			return fmt.Errorf("missing %s in discovery", tool)
		}
	}
	hunter := na.AsString(s.Prepared()["hunter"])
	observe := func(label string) (map[string]any, error) { return h.Call(ctx, label, chainObserveOp, nil) }
	status := func(label string) (map[string]any, error) {
		reply, err := h.Wire(ctx, label, "rules_read_status", map[string]any{"identity": identity})
		if err != nil {
			return nil, err
		}
		_, body, err := na.Outcome(reply, "status")
		return body, err
	}
	recordsAtLeast := func(label string, n int) error {
		_, err := na.RunUntil(ctx, h, label, 6000, na.Wait{Stall: na.StallBudget()}, func(ctx context.Context) (string, bool, error) {
			v, err := observe(label + "-progress")
			if err != nil {
				return "", false, err
			}
			s.Report()["chain_tally"] = v
			return na.Signature(v["tick"], v["kills"]), len(na.AsSlice(v["records"])) >= n, nil
		})
		return err
	}
	record := func(i int) (map[string]any, error) {
		v, err := observe("record")
		if err != nil {
			return nil, err
		}
		rows := na.AsSlice(v["records"])
		if len(rows) <= i {
			return nil, fmt.Errorf("want kill record %d, have %d: %v", i, len(rows), v)
		}
		row, _ := na.AsMap(rows[i])
		return row, nil
	}
	// The fixture stages pawns outside an owned scope, so authority is granted after it.
	if _, err := na.GrantAuto(ctx, h.WireFunc(), "grant", identity); err != nil {
		return err
	}
	rule := func(id, job string) map[string]any {
		return map[string]any{"id": id, "trigger": "RULE_TRIGGER_PREY_KILLED",
			"predicates": []string{"RULE_PREDICATE_ACTOR_UNDRAFTED", "RULE_PREDICATE_ACTOR_HUNTING_WORK_ACTIVE", "RULE_PREDICATE_TARGET_AVAILABLE"},
			"action":     "RULE_ACTION_GIVE_JOB", "job": job, "target": "RULE_TARGET_SELECTOR_NEAREST_DESIGNATED_PREY", "radius": 80}
	}
	attach := func(label string, lease float64, rules ...map[string]any) (map[string]any, error) {
		start, err := observe(label + "-tick")
		if err != nil {
			return nil, err
		}
		reply, err := h.Wire(ctx, label, "rules_attach", map[string]any{"identity": identity, "rules": rules,
			"expiresAtTick": fmt.Sprint(int64(na.AsNumber(start["tick"]) + lease))})
		if err != nil {
			return nil, err
		}
		_, body, err := na.Outcome(reply, "attached")
		return body, err
	}

	// The whitelist refuses a draft job; the probe attaches nothing that lasts.
	attached, err := attach("attach", chainLease, rule(chainID, "Hunt"), rule("draft", "Draft"))
	if err != nil {
		return err
	}
	refused := na.AsSlice(attached["refused"])
	if ids := na.AsSlice(attached["acceptedIds"]); len(ids) != 1 || na.AsString(ids[0]) != chainID || len(refused) != 1 {
		return fmt.Errorf("want only %s accepted and the draft rule refused: %v", chainID, attached)
	}
	if row, _ := na.AsMap(refused[0]); na.AsString(row["ruleId"]) != "draft" || na.AsString(row["reason"]) != "RULE_REFUSAL_REASON_UNSUPPORTED_JOB" {
		return fmt.Errorf("the draft rule must be refused as an unsupported job: %v", refused[0])
	}
	if _, err := h.Wire(ctx, "probe-clear", "rules_clear", map[string]any{"identity": identity}); err != nil {
		return err
	}

	// Rule active: the controller attaches the hunt-chain rule itself, its
	// rules_attach receipt in the journal, and the hunter chains to the next prey.
	phase, err := planstage.Begin(ctx, s, nil, nil)
	if err != nil {
		return err
	}
	if err = phase.Until(ctx, "the controller attaches the hunt-chain rule", func(store.Rounds) (string, bool, error) { return rulesJournaled(ctx, phase.St) }); err != nil {
		phase.Abort()
		return err
	}
	if h, err = phase.Stop(ctx, "rules_service"); err != nil {
		return err
	}
	if _, err := na.GrantAuto(ctx, h.WireFunc(), "regrant", identity); err != nil {
		return err
	}
	standing, err := status("controller-status")
	if err != nil {
		return err
	}
	active := na.AsSlice(standing["rules"])
	if row, _ := na.AsMap(first(active)); len(active) != 1 || na.AsString(row["ruleId"]) != chainID || na.AsNumber(standing["leaseRemainingTicks"]) <= 0 {
		return fmt.Errorf("want the controller's %s rule active under its lease: %v", chainID, standing)
	}
	s.Report()["chain_attached"] = standing
	if err := recordsAtLeast("chain-first", 1); err != nil {
		return err
	}
	first, err := record(0)
	if err != nil {
		return err
	}
	if na.AsString(first["jobDef"]) != "Hunt" || !flag(first["targetLive"]) || !flag(first["targetDesignated"]) ||
		flag(first["targetIsKilled"]) || flag(first["carryingCorpse"]) {
		return fmt.Errorf("after the first kill the hunter must be on Hunt against another live designated deer, not hauling its kill: %v", first)
	}
	fired, err := status("fired-status")
	if err != nil {
		return err
	}
	rules := na.AsSlice(fired["rules"])
	if len(rules) != 1 {
		return fmt.Errorf("want one active rule: %v", fired)
	}
	row, _ := na.AsMap(rules[0])
	if na.AsNumber(row["firingCount"]) != 1 || na.AsString(row["lastActorId"]) != hunter || na.AsString(row["lastTargetId"]) != na.AsString(first["targetId"]) {
		return fmt.Errorf("the status must name the one firing, its hunter and its target: %v", row)
	}
	s.Report()["chain_fired"] = map[string]any{"record": first, "status": fired}

	// Cleared: vanilla behaviour persists on the next kill.
	cleared, err := h.Wire(ctx, "clear", "rules_clear", map[string]any{"identity": identity})
	if err != nil {
		return err
	}
	if _, body, err := na.Outcome(cleared, "cleared"); err != nil || na.AsNumber(body["cleared"]) != 1 {
		return fmt.Errorf("clear must report the one active rule: %v %v", cleared, err)
	}
	if err := recordsAtLeast("chain-cleared", 2); err != nil {
		return err
	}
	second, err := record(1)
	if err != nil {
		return err
	}
	if flag(second["targetLive"]) {
		return fmt.Errorf("with the rules cleared the hunter must keep vanilla behaviour after its kill: %v", second)
	}
	after, err := status("cleared-status")
	if err != nil {
		return err
	}
	if len(na.AsSlice(after["rules"])) != 0 {
		return fmt.Errorf("cleared rules must not stay active: %v", after)
	}

	// Lease expired: nothing fires on the third kill although prey remains designated.
	if _, err := attach("attach-short", 10, rule(chainID, "Hunt")); err != nil {
		return err
	}
	if _, err := na.RunUntil(ctx, h, "chain-expiry", 600, na.Wait{Stall: na.StallBudget()}, func(ctx context.Context) (string, bool, error) {
		v, err := status("expiry-status")
		if err != nil {
			return "", false, err
		}
		expired, _ := na.AsBool(v["leaseExpired"])
		return na.Signature(v["leaseRemainingTicks"]), expired, nil
	}); err != nil {
		return err
	}
	if err := recordsAtLeast("chain-expired", 3); err != nil {
		return err
	}
	third, err := record(2)
	if err != nil {
		return err
	}
	if flag(third["targetLive"]) {
		return fmt.Errorf("after the lease expired nothing may fire: %v", third)
	}
	final, err := status("expired-status")
	if err != nil {
		return err
	}
	if expired, _ := na.AsBool(final["leaseExpired"]); !expired || len(na.AsSlice(final["rules"])) != 0 {
		return fmt.Errorf("the expired lease must leave no active rule: %v", final)
	}
	s.Report()["chain_expired"] = map[string]any{"record": third, "status": final}
	return nil
}

func flag(v any) bool { b, _ := na.AsBool(v); return b }

func first(rows []any) any {
	if len(rows) == 0 {
		return nil
	}
	return rows[0]
}

// rulesJournaled reports whether the journal holds a completed rules_attach
// carrying the hunt-chain rule: the intent's receipt, written before native was.
func rulesJournaled(ctx context.Context, st *store.Store) (string, bool, error) {
	plans, err := st.LoadPlans(ctx)
	if err != nil {
		return "", false, err
	}
	for _, plan := range plans {
		for _, progress := range plan.Progress {
			attach, ok := progress.Action().RulesAttach()
			if !ok || progress.View().Stage != domain.Completed {
				continue
			}
			for _, rule := range attach.Rules() {
				if rule.ID == chainID {
					return "attached", true, nil
				}
			}
		}
	}
	return "waiting", false, nil
}
