// Package combat holds the Loud combat cases (the former combataccept):
// bounded actual combat terminal outcome through the scenario clock's
// WATCH_MODE_COMBAT policy, with player override/fresh claim, replay, and
// completed-before-Manual retention. No damage or completion injection;
// the terminal outcome must be causally verified by the native runtime
// itself within a bounded shared-clock tick budget.
package combat

import (
	"context"
	"fmt"
	"strings"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

const (
	combatWindows    = 20
	ticksPerWindow   = 240
	combatTickBudget = combatWindows * ticksPerWindow
)

// loudReason is why every combat case keeps the storyteller: the fixture
// spawns hostile animals and the WATCH_MODE_COMBAT clock must see them as
// the game reports hostility, which the quiet op's storyteller swap would
// mask.
const loudReason = "the attack target is a fixture-spawned hostile animal and the combat watch policy stops on the game's own hostility and health reporting"

func init() {
	for _, v := range []struct {
		name              string
		ranged, explosive bool
	}{
		{"combat/melee", false, false},
		{"combat/ranged", true, false},
		{"combat/explosive", true, true},
	} {
		v := v
		cases.Register(cases.Case{
			Name: v.name,
			Scope: "Actual attributed combat terminal outcome, player override and fresh claim, replay, " +
				"completed-before-Manual retention; bounded shared clock waits. No damage or completion injection.",
			Start:  cases.DebugStart{},
			Quiet:  na.Loud,
			Reason: loudReason,
			Budget: 12 * time.Minute,
			Run: func(ctx context.Context, s cases.Session) error {
				return run(ctx, s, v.ranged, v.explosive)
			},
		})
	}
}

func run(ctx context.Context, s cases.Session, ranged, explosive bool) error {
	report := s.Report()
	report["ranged"] = ranged
	report["explosive"] = explosive
	report["combat_tick_budget"] = combatTickBudget
	mode := "ATTACK_MODE_MELEE"
	jobDef := "AttackMelee"
	if ranged {
		mode = "ATTACK_MODE_RANGED"
		jobDef = "AttackStatic"
	}
	h, names, identity := s.Harness(), s.Names(), s.Identity()
	identityReply, err := h.Wire(ctx, "identity-paused", "lifecycle_read_identity", map[string]any{})
	if err != nil {
		return err
	}
	_, initial, err := na.Outcome(identityReply, "loaded")
	if err != nil {
		return err
	}
	if paused, _ := na.AsBool(initial["paused"]); !paused {
		return fmt.Errorf("fresh debug game did not start paused")
	}
	initialContext, _ := na.AsMap(initial["context"])

	query := func(label string, filters map[string]any) (map[string]any, error) {
		return h.Wire(ctx, label, "observations_list_pawns", map[string]any{
			"scope": map[string]any{"expectedIdentity": identity}, "filter": filters,
		})
	}
	read := func(label, pawnID string) (map[string]any, error) {
		reply, err := query(label, map[string]any{"ids": []any{pawnID}})
		if err != nil {
			return nil, err
		}
		return na.PawnRow(reply, identity, pawnID)
	}

	healthyReply, err := query("healthy-colonists", map[string]any{"colonist": true})
	if err != nil {
		return err
	}
	healthyRows, err := observedRows(healthyReply, identity)
	if err != nil {
		return err
	}
	people, err := healthyCandidates(healthyRows, ranged)
	if err != nil {
		return err
	}
	if len(people) == 0 {
		return fmt.Errorf("no healthy observed violence-capable colonist")
	}
	actorPawn, _ := na.AsMap(people[0]["pawn"])
	actorID := na.AsString(actorPawn["id"])

	if ranged {
		gearOp := "ranged-equipment"
		wantDefName := "Gun_AssaultRifle"
		if explosive {
			gearOp = "explosive-equipment"
			wantDefName = "Weapon_GrenadeFrag"
		}
		gear, err := h.Call(ctx, gearOp, "test/b04f_setup", map[string]any{"op": gearOp, "pawn": actorID})
		if err != nil {
			return err
		}
		if success, _ := na.AsBool(gear["success"]); !success {
			return fmt.Errorf("%s fixture refused", gearOp)
		}
		if injected, _ := na.AsBool(gear["completedWorkInjected"]); injected {
			return fmt.Errorf("%s fixture injected completed work", gearOp)
		}
		weapons := na.AsSlice(gear["weapons"])
		if len(weapons) != 1 {
			return fmt.Errorf("%s fixture did not equip exactly one weapon: %#v", gearOp, weapons)
		}
		weaponID := na.AsString(weapons[0])
		equipped, err := read("equipped-attacker", actorID)
		if err != nil {
			return err
		}
		equipment, _ := na.AsMap(equipped["equipment"])
		if na.AsString(equipment["primaryId"]) != weaponID {
			return fmt.Errorf("equipped attacker's primaryId does not match the fixture weapon")
		}
		found := false
		for _, raw := range na.AsSlice(equipment["equipped"]) {
			item, _ := na.AsMap(raw)
			thing, _ := na.AsMap(item["thing"])
			if na.AsString(thing["id"]) == weaponID && na.AsString(thing["defName"]) == wantDefName {
				found = true
				break
			}
		}
		if !found {
			return fmt.Errorf("equipped attacker's equipment does not include %s (%s)", weaponID, wantDefName)
		}
	}

	setupOp := "opponents"
	if explosive {
		setupOp = "explosive-opponents"
	} else if ranged {
		setupOp = "ranged-opponents"
	}
	setup, err := h.Call(ctx, "opponents", "test/b04f_setup", map[string]any{"op": setupOp, "pawn": actorID})
	if err != nil {
		return err
	}
	if success, _ := na.AsBool(setup["success"]); !success {
		return fmt.Errorf("%s fixture refused", setupOp)
	}
	if injected, _ := na.AsBool(setup["completedWorkInjected"]); injected {
		return fmt.Errorf("%s fixture injected completed work", setupOp)
	}
	targets := stringSlice(setup["opponents"])
	if len(targets) != 2 || len(uniqueStrings(targets)) != 2 {
		return fmt.Errorf("expected exactly 2 distinct opponents, got %#v", targets)
	}
	opponentsReply, err := query("observed-opponents", map[string]any{"ids": anySlice(targets)})
	if err != nil {
		return err
	}
	targetsBefore, err := observedRows(opponentsReply, identity)
	if err != nil {
		return err
	}
	if !sameIDSet(targetsBefore, targets) {
		return fmt.Errorf("observed opponents do not match the setup fixture's targets")
	}
	wantDefName := "Hare"
	if ranged {
		wantDefName = "Tortoise"
	}
	for _, r := range targetsBefore {
		pawn, _ := na.AsMap(r["pawn"])
		animal, _ := na.AsBool(r["animal"])
		hostile, _ := na.AsBool(r["hostile"])
		dead, _ := na.AsBool(r["dead"])
		downed, _ := na.AsBool(r["downed"])
		if na.AsString(pawn["defName"]) != wantDefName || !animal || !hostile || dead || downed {
			return fmt.Errorf("opponent does not match expected fixture shape: %#v", r)
		}
	}

	actor, err := read("attacker-before", actorID)
	if err != nil {
		return err
	}
	victim, err := read("target-before", targets[0])
	if err != nil {
		return err
	}
	grant, err := na.GrantAuto(ctx, h.WireFunc(), "acquire", identity)
	if err != nil {
		return err
	}
	draftReply, err := h.Wire(ctx, "draft", "operations_execute", na.ExecuteRequest(identity, grant, actor, 1))
	if err != nil {
		return err
	}
	_, drafted, err := na.Outcome(draftReply, "receipt")
	if err != nil {
		return err
	}
	actor, err = read("owned-attacker", actorID)
	if err != nil {
		return err
	}
	if err := na.OwnedEffect(drafted, actor, "applied", true); err != nil {
		return err
	}

	request := attackRequest(identity, grant, actor, victim, 3, mode)
	operation, _ := na.AsMap(request["operation"])
	previewReply, err := h.Wire(ctx, "attack-preview", "operations_preview", map[string]any{"identity": identity, "operation": operation})
	if err != nil {
		return err
	}
	_, preview, err := na.Outcome(previewReply, "evaluated")
	if err != nil {
		return err
	}
	if accepted, _ := na.AsBool(preview["accepted"]); !accepted {
		return fmt.Errorf("attack preview was not accepted: %#v", preview)
	}
	receiptReply, err := h.Wire(ctx, "attack", "operations_execute", request)
	if err != nil {
		return err
	}
	_, receipt, err := na.Outcome(receiptReply, "receipt")
	if err != nil {
		return err
	}
	if err := checkAttackEffect(receipt, "applied", actorID, targets[0], jobDef); err != nil {
		return fmt.Errorf("attack: %w", err)
	}
	attacking, err := read("attack-job", actorID)
	if err != nil {
		return err
	}
	appliedJob, _ := na.AsMap(receipt["applied"])
	appliedObserved, _ := na.AsMap(appliedJob["observed"])
	appliedEffect, _ := na.AsMap(appliedObserved["job"])
	attackingJob, _ := na.AsMap(attacking["job"])
	if na.AsString(attackingJob["loadId"]) != fmt.Sprint(appliedEffect["jobId"]) || na.AsString(attackingJob["defName"]) != jobDef {
		return fmt.Errorf("attack-job: row job does not match the issued effect")
	}
	precondition, _ := na.AsMap(request["precondition"])
	attempt := map[string]any{"identity": identity, "attempt": precondition["attempt"]}
	pendingReply, err := h.Wire(ctx, "attack-pending", "receipts_observe_progress", attempt)
	if err != nil {
		return err
	}
	_, pending, err := na.Outcome(pendingReply, "progress")
	if err != nil {
		return err
	}
	if _, ok := pending["pending"]; !ok {
		return fmt.Errorf("attack-pending: expected a pending case, got %#v", pending)
	}
	replayReply, err := h.Wire(ctx, "attack-replay", "operations_execute", request)
	if err != nil {
		return err
	}
	_, replay, err := na.Outcome(replayReply, "receipt")
	if err != nil {
		return err
	}
	if !na.DeepEqual(replay, receipt) {
		return fmt.Errorf("attack-replay returned a different receipt than the original execute")
	}
	replayRow, err := read("replay-unchanged", actorID)
	if err != nil {
		return err
	}
	if err := na.SameControl(attacking, replayRow); err != nil {
		return fmt.Errorf("replay-unchanged: %w", err)
	}
	if !na.DeepEqual(replayRow["job"], attacking["job"]) {
		return fmt.Errorf("replay-unchanged: job changed unexpectedly")
	}

	external, err := h.Call(ctx, "player-override", "test/b04f_setup", map[string]any{"op": "external-order", "pawn": actorID})
	if err != nil {
		return err
	}
	player, err := read("player-job", actorID)
	if err != nil {
		return err
	}
	overrideProgressReply, err := h.Wire(ctx, "override-progress", "receipts_observe_progress", attempt)
	if err != nil {
		return err
	}
	_, interrupted, err := na.Outcome(overrideProgressReply, "progress")
	if err != nil {
		return err
	}
	if err := overriddenAttack(interrupted, attacking, player, external); err != nil {
		return err
	}
	report["interrupted_attempt"] = attempt
	report["interrupted_progress"] = interrupted

	grant, err = na.GrantAuto(ctx, h.WireFunc(), "unowned-acquire", identity)
	if err != nil {
		return err
	}
	refusalReply, err := h.Wire(ctx, "unowned-draft-refused", "operations_execute", na.ExecuteRequest(identity, grant, player, 4))
	if err != nil {
		return err
	}
	_, refusal, err := na.Outcome(refusalReply, "failure")
	if err != nil {
		return err
	}
	if na.AsString(refusal["code"]) != "FAILURE_CODE_OWNER_CONFLICT" {
		return fmt.Errorf("unowned-draft-refused: expected FAILURE_CODE_OWNER_CONFLICT, got %q", refusal["code"])
	}
	preserved, err := read("player-job-preserved", actorID)
	if err != nil {
		return err
	}
	if err := na.SameControl(player, preserved); err != nil {
		return fmt.Errorf("player-job-preserved: %w", err)
	}
	if err := na.ActualOrder(external, preserved); err != nil {
		return err
	}

	undraft, err := h.Call(ctx, "player-undraft", "test/b04f_setup", map[string]any{"op": "external-draft", "pawn": actorID, "drafted": false})
	if err != nil {
		return err
	}
	if success, _ := na.AsBool(undraft["success"]); !success {
		return fmt.Errorf("player-undraft fixture refused")
	}
	if after, _ := na.AsBool(undraft["after"]); after {
		return fmt.Errorf("player-undraft fixture did not clear drafted")
	}
	actor, err = read("fresh-undrafted", actorID)
	if err != nil {
		return err
	}
	if !na.DeepEqual(actor["draftClaim"], map[string]any{"unowned": map[string]any{}}) {
		return fmt.Errorf("fresh-undrafted: draft claim is not unowned")
	}
	if drafted, _ := na.AsBool(actor["drafted"]); drafted {
		return fmt.Errorf("fresh-undrafted: pawn is still drafted")
	}
	grant, err = na.GrantAuto(ctx, h.WireFunc(), "fresh-acquire", identity)
	if err != nil {
		return err
	}
	freshDraftReply, err := h.Wire(ctx, "fresh-draft", "operations_execute", na.ExecuteRequest(identity, grant, actor, 5))
	if err != nil {
		return err
	}
	_, freshDraft, err := na.Outcome(freshDraftReply, "receipt")
	if err != nil {
		return err
	}
	actor, err = read("fresh-owned-attacker", actorID)
	if err != nil {
		return err
	}
	if err := na.OwnedEffect(freshDraft, actor, "applied", true); err != nil {
		return err
	}
	actorClaim, _ := na.AsMap(actor["draftClaim"])
	actorOwned, _ := na.AsMap(actorClaim["owned"])
	attackingClaim, _ := na.AsMap(attacking["draftClaim"])
	attackingOwned, _ := na.AsMap(attackingClaim["owned"])
	if na.AsString(actorOwned["claimId"]) == na.AsString(attackingOwned["claimId"]) {
		return fmt.Errorf("fresh draft reused the original owned claim id")
	}

	victim, err = read("fresh-target-snapshot", targets[0])
	if err != nil {
		return err
	}
	request = attackRequest(identity, grant, actor, victim, 6, mode)
	freshAttackReply, err := h.Wire(ctx, "fresh-attack", "operations_execute", request)
	if err != nil {
		return err
	}
	_, receipt, err = na.Outcome(freshAttackReply, "receipt")
	if err != nil {
		return err
	}
	if err := checkAttackEffect(receipt, "applied", actorID, targets[0], jobDef); err != nil {
		return fmt.Errorf("fresh-attack: %w", err)
	}
	precondition, _ = na.AsMap(request["precondition"])
	attempt = map[string]any{"identity": identity, "attempt": precondition["attempt"]}
	freshPendingReply, err := h.Wire(ctx, "fresh-pending", "receipts_observe_progress", attempt)
	if err != nil {
		return err
	}
	_, freshPending, err := na.Outcome(freshPendingReply, "progress")
	if err != nil {
		return err
	}
	if _, ok := freshPending["pending"]; !ok {
		return fmt.Errorf("fresh-pending: expected a pending case, got %#v", freshPending)
	}

	supervisor := &na.ScenarioClock{
		Wire: func(ctx context.Context, label, method string, request map[string]any) (map[string]any, error) {
			return h.Wire(ctx, label, method, request)
		},
		Identity: identity, Owner: na.AsString(na.Owner["controllerSessionId"]), Report: report, Grant: grant, CombatTargets: targets,
	}
	rt := &na.ScenarioRuntime{Query: h.Call, Clock: supervisor, Report: report, Tools: names, CombatTargets: targets}

	var completed map[string]any
	var progress map[string]any
	var victims []map[string]any
	window := 0
	for ; window < combatWindows; window++ {
		if err := supervisor.RenewAuthority(ctx); err != nil {
			return err
		}
		if _, err := na.AdvanceGame(ctx, rt, ticksPerWindow, na.WithTimeout(180*time.Second), na.WithCombatTargets(targets...)); err != nil {
			return err
		}
		progressReply, err := h.Wire(ctx, fmt.Sprintf("progress-%d", window), "receipts_observe_progress", attempt)
		if err != nil {
			return err
		}
		_, progress, err = na.Outcome(progressReply, "progress")
		if err != nil {
			return err
		}
		targetStateReply, err := query(fmt.Sprintf("target-state-%d", window), map[string]any{"ids": []any{targets[0]}, "includeDead": true})
		if err != nil {
			return err
		}
		victims, err = observedRows(targetStateReply, identity)
		if err != nil {
			return err
		}
		if len(victims) != 1 {
			return fmt.Errorf("exact native target state unavailable: %#v", victims)
		}
		attackerState, err := read(fmt.Sprintf("attacker-state-%d", window), actorID)
		if err != nil {
			return err
		}
		report["last_attacker"] = attackerState
		report["last_progress"] = progress
		report["last_target"] = victims[0]
		if _, ok := progress["completed"]; ok {
			if err := terminal(progress, receipt, victims[0], targets[0], ranged); err != nil {
				return err
			}
			completed = progress
			break
		}
		if _, ok := progress["pending"]; !ok {
			return fmt.Errorf("window %d: expected a pending or completed progress, got %#v", window, progress)
		}
	}
	report["combat_windows_used"] = window + 1
	report["last_combat_progress"] = progress
	report["last_target_state"] = victims[0]
	if completed == nil {
		return fmt.Errorf("combat budget exhausted after %d ticks without causally verified terminal damage", combatTickBudget)
	}

	manualStatusReply, err := h.Wire(ctx, "manual-status", "authority_read_status", map[string]any{"identity": identity})
	if err != nil {
		return err
	}
	_, manualStatus, err := na.Outcome(manualStatusReply, "status")
	if err != nil {
		return err
	}
	manualStatusContext, _ := na.AsMap(manualStatus["context"])
	manualRevokeReply, err := h.Wire(ctx, "manual-after-completion", "authority_control", map[string]any{"revoke": map[string]any{
		"identity": identity, "expectedGeneration": manualStatusContext["nativeGeneration"], "reason": "REVOCATION_REASON_MANUAL",
	}})
	if err != nil {
		return err
	}
	if _, _, err := na.Outcome(manualRevokeReply, "revoked"); err != nil {
		return err
	}
	retainedReply, err := h.Wire(ctx, "completed-after-manual", "receipts_observe_progress", attempt)
	if err != nil {
		return err
	}
	_, retained, err := na.Outcome(retainedReply, "progress")
	if err != nil {
		return err
	}
	if complete, _ := na.AsBool(retained["completeInspection"]); !complete {
		return fmt.Errorf("completed-after-manual: not a complete inspection")
	}
	if !na.DeepEqual(retained["completed"], completed["completed"]) {
		return fmt.Errorf("completed-after-manual: retained completion does not match")
	}
	actor, err = read("cleanup-attacker", actorID)
	if err != nil {
		return err
	}
	cleanup, err := na.ReleaseRequest(identity, actor)
	if err != nil {
		return err
	}
	releasedReply, err := h.Wire(ctx, "cleanup-draft", "operations_release_owned_draft", cleanup)
	if err != nil {
		return err
	}
	_, released, err := na.Outcome(releasedReply, "released")
	if err != nil {
		return err
	}
	releasedObserved, _ := na.AsMap(released["observed"])
	if drafted, _ := na.AsBool(releasedObserved["drafted"]); drafted {
		return fmt.Errorf("cleanup-draft did not clear drafted")
	}
	if verified, _ := na.AsBool(releasedObserved["verified"]); !verified {
		return fmt.Errorf("cleanup-draft was not verified")
	}
	immutableReply, err := h.Wire(ctx, "immutable-attack-replay", "operations_execute", request)
	if err != nil {
		return err
	}
	_, immutable, err := na.Outcome(immutableReply, "receipt")
	if err != nil {
		return err
	}
	if !na.DeepEqual(immutable, receipt) {
		return fmt.Errorf("immutable-attack-replay returned a different receipt than the original execute")
	}
	finalReply, err := h.Wire(ctx, "identity-after", "lifecycle_read_identity", map[string]any{})
	if err != nil {
		return err
	}
	_, final, err := na.Outcome(finalReply, "loaded")
	if err != nil {
		return err
	}
	if paused, _ := na.AsBool(final["paused"]); !paused {
		return fmt.Errorf("game unexpectedly resumed")
	}
	finalContext, _ := na.AsMap(final["context"])
	if !na.DeepEqual(finalContext["identity"], identity) {
		return fmt.Errorf("identity changed during the run")
	}
	ticks := na.AsNumber(finalContext["tick"]) - na.AsNumber(initialContext["tick"])
	if ticks <= 0 || ticks > combatTickBudget {
		return fmt.Errorf("unexpected tick delta: %v", ticks)
	}
	if err := cases.CheckStartupLog(s); err != nil {
		return err
	}
	report["pawn_id"] = actorID
	report["target_ids"] = targets
	report["completed"] = completed
	report["ticks"] = ticks
	return nil
}

// attackRequest builds an operations_execute attackTarget request at grant's
// generation.
func attackRequest(identity, grant, actor, victim map[string]any, number int, mode string) map[string]any {
	request := na.ExecuteRequest(identity, grant, actor, number)
	request["operation"] = map[string]any{"attackTarget": map[string]any{
		"pawn": na.Target(actor), "target": na.Target(victim), "mode": mode,
		"requireHostile": true, "requireStanding": true, "requireCombatHealth": true,
	}}
	return request
}

// checkAttackEffect asserts an operations_execute receipt's named case describes an
// issued, verified attack job against targetID.
func checkAttackEffect(receipt map[string]any, caseName, actorID, targetID, jobDef string) error {
	caseValue, _ := na.AsMap(receipt[caseName])
	observed, _ := na.AsMap(caseValue["observed"])
	effect, _ := na.AsMap(observed["job"])
	issued, _ := na.AsBool(effect["issued"])
	verified, _ := na.AsBool(effect["verified"])
	if !issued || !verified || na.AsString(effect["jobDef"]) != jobDef {
		return fmt.Errorf("effect issued/verified/jobDef mismatch: %#v", effect)
	}
	targetA, _ := na.AsMap(effect["targetA"])
	if na.AsString(targetA["thingId"]) != targetID || na.AsString(effect["pawnId"]) != actorID {
		return fmt.Errorf("effect target/pawn mismatch: %#v", effect)
	}
	return nil
}

// terminal asserts progress records a causally verified terminal attack outcome
// against victim/targetID.
func terminal(progress, receipt, victim map[string]any, targetID string, ranged bool) error {
	complete, _ := na.AsBool(progress["completeInspection"])
	completed, ok := na.AsMap(progress["completed"])
	if !complete || !ok {
		return fmt.Errorf("expected a complete terminal progress: %#v", progress)
	}
	applied, _ := na.AsMap(receipt["applied"])
	appliedObserved, _ := na.AsMap(applied["observed"])
	original, _ := na.AsMap(appliedObserved["job"])
	evidence, _ := na.AsMap(completed["evidence"])
	effect, _ := na.AsMap(evidence["job"])
	wantJobDef := "AttackMelee"
	if ranged {
		wantJobDef = "AttackStatic"
	}
	if fmt.Sprint(effect["jobId"]) != fmt.Sprint(original["jobId"]) || na.AsString(effect["jobDef"]) != wantJobDef {
		return fmt.Errorf("terminal jobId/jobDef mismatch: %#v", effect)
	}
	if na.AsString(effect["pawnId"]) != na.AsString(original["pawnId"]) {
		return fmt.Errorf("terminal pawnId mismatch")
	}
	targetA, _ := na.AsMap(effect["targetA"])
	if na.AsString(targetA["thingId"]) != targetID {
		return fmt.Errorf("terminal targetA mismatch: %#v", effect)
	}
	if verified, _ := na.AsBool(effect["verified"]); !verified {
		return fmt.Errorf("terminal effect was not verified")
	}
	pawn, _ := na.AsMap(victim["pawn"])
	if na.AsString(pawn["id"]) != targetID {
		return fmt.Errorf("victim id does not match targetID")
	}
	dead, _ := na.AsBool(victim["dead"])
	downed, _ := na.AsBool(victim["downed"])
	if !dead && !downed {
		return fmt.Errorf("victim is neither dead nor downed")
	}
	reason := na.AsString(effect["verifiedReason"])
	if !strings.HasPrefix(reason, "Native positive damage by this exact attacker/job caused") {
		return fmt.Errorf("unexpected verifiedReason: %q", reason)
	}
	return nil
}

// overriddenAttack asserts a player override interrupted the pending attack.
func overriddenAttack(progress, before, after, external map[string]any) error {
	complete, _ := na.AsBool(progress["completeInspection"])
	if !complete {
		return fmt.Errorf("expected a complete progress inspection: %#v", progress)
	}
	unsuccessful, _ := na.AsMap(progress["unsuccessful"])
	if na.AsString(unsuccessful["reason"]) != "UNSUCCESSFUL_REASON_INTERRUPTED" {
		return fmt.Errorf("expected UNSUCCESSFUL_REASON_INTERRUPTED, got %#v", progress)
	}
	if !na.DeepEqual(after["draftClaim"], map[string]any{"unowned": map[string]any{}}) {
		return fmt.Errorf("override: draft claim was not cleared")
	}
	if drafted, _ := na.AsBool(after["drafted"]); !drafted {
		return fmt.Errorf("override: pawn is no longer drafted")
	}
	if na.DeepEqual(na.Target(before), na.Target(after)) {
		return fmt.Errorf("override: snapshot token did not rotate")
	}
	return na.ActualOrder(external, after)
}

// observedRows asserts an observations_list_pawns reply is a single complete, exact-
// match page with unique pawn ids and returns its rows.
func observedRows(reply, identity map[string]any) ([]map[string]any, error) {
	_, observed, err := na.Outcome(reply, "observed")
	if err != nil {
		return nil, err
	}
	observedContext, _ := na.AsMap(observed["context"])
	if !na.DeepEqual(observedContext["identity"], identity) {
		return nil, fmt.Errorf("observed rows: context identity mismatch")
	}
	rowsRaw := na.AsSlice(observed["pawns"])
	completeness, _ := na.AsMap(observed["completeness"])
	page, _ := na.AsMap(completeness["page"])
	if complete, _ := na.AsBool(page["complete"]); !complete || na.AsNumber(completeness["unreadable"]) != 0 {
		return nil, fmt.Errorf("observed rows: incomplete or unreadable page: %#v", completeness)
	}
	matched, returned := na.AsNumber(completeness["matched"]), na.AsNumber(completeness["returned"])
	if matched != returned || int(returned) != len(rowsRaw) {
		return nil, fmt.Errorf("observed rows: completeness count mismatch: %#v", completeness)
	}
	rows := make([]map[string]any, 0, len(rowsRaw))
	seen := map[string]bool{}
	for _, raw := range rowsRaw {
		row, _ := na.AsMap(raw)
		pawn, _ := na.AsMap(row["pawn"])
		id := na.AsString(pawn["id"])
		if seen[id] {
			return nil, fmt.Errorf("observed rows: duplicate pawn id %q", id)
		}
		seen[id] = true
		rows = append(rows, row)
	}
	return rows, nil
}

// healthyCandidates filters rows to violence-capable, undrafted, healthy colonists
// (and shooting-capable when ranged).
func healthyCandidates(rows []map[string]any, ranged bool) ([]map[string]any, error) {
	if len(rows) == 0 {
		return nil, fmt.Errorf("no rows provided")
	}
	for _, r := range rows {
		dead, _ := na.AsBool(r["dead"])
		downed, _ := na.AsBool(r["downed"])
		health, _ := na.AsMap(r["health"])
		fraction := na.AsNumber(health["summaryFraction"])
		if dead || downed || fraction <= .5005 {
			return nil, fmt.Errorf("healthy candidates: row failed health precondition: %#v", r)
		}
	}
	var result []map[string]any
	for _, r := range rows {
		if drafted, _ := na.AsBool(r["drafted"]); drafted {
			continue
		}
		biography, _ := na.AsMap(r["biography"])
		tags := stringSlice(biography["disabledWorkTags"])
		if contains(tags, "Violent") {
			continue
		}
		if ranged && contains(tags, "Shooting") {
			continue
		}
		skip := false
		for _, raw := range na.AsSlice(biography["issues"]) {
			issue, _ := na.AsMap(raw)
			if na.AsString(issue["field"]) == "disabled_work_tags" {
				skip = true
				break
			}
		}
		if skip {
			continue
		}
		result = append(result, r)
	}
	return result, nil
}

func sameIDSet(rows []map[string]any, ids []string) bool {
	seen := map[string]bool{}
	for _, r := range rows {
		pawn, _ := na.AsMap(r["pawn"])
		seen[na.AsString(pawn["id"])] = true
	}
	if len(seen) != len(ids) {
		return false
	}
	for _, id := range ids {
		if !seen[id] {
			return false
		}
	}
	return true
}

func stringSlice(v any) []string {
	raw := na.AsSlice(v)
	out := make([]string, len(raw))
	for i, item := range raw {
		out[i] = na.AsString(item)
	}
	return out
}

func uniqueStrings(values []string) []string {
	seen := map[string]bool{}
	var out []string
	for _, v := range values {
		if !seen[v] {
			seen[v] = true
			out = append(out, v)
		}
	}
	return out
}

func anySlice(values []string) []any {
	out := make([]any, len(values))
	for i, v := range values {
		out[i] = v
	}
	return out
}

func contains(values []string, want string) bool {
	for _, v := range values {
		if v == want {
			return true
		}
	}
	return false
}
