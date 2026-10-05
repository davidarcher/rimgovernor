// Package combat holds the Loud combat cases (the former combataccept):
// a colonist drafted by a DraftIntent attacks through one combat_orders
// intent (#939), native picking melee or ranged from its weapon; the
// same key replays the receipt; the scenario clock's WATCH_MODE_COMBAT
// policy runs bounded windows until the target is down, and the undraft
// intent releases the attacker. No damage or completion injection.
package combat

import (
	"context"
	"fmt"
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
			Scope: "Draft intent, one combat_orders attack (melee or ranged by weapon), key replay, " +
				"target downed or dead within bounded shared clock waits, undraft intent. No damage or completion injection.",
			Start:  cases.LabStart(),
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
	jobDef := "AttackMelee"
	if ranged {
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

	grant, err := na.GrantAuto(ctx, h.WireFunc(), "acquire", identity)
	if err != nil {
		return err
	}
	// Drafts are plan-owned intents (#939): draft, then one combat_orders
	// attack; native picks melee or ranged from the attacker's weapon.
	if job, err := na.ApplyDraft(ctx, h, "draft", identity, "combat-draft", actorID, true); err != nil {
		return err
	} else if issued, _ := na.AsBool(job["issued"]); !issued {
		return fmt.Errorf("draft: an undrafted attacker was not drafted: %#v", job)
	}
	actor, err := read("drafted-attacker", actorID)
	if err != nil {
		return err
	}
	if drafted, _ := na.AsBool(actor["drafted"]); !drafted {
		return fmt.Errorf("drafted-attacker: census shows the attacker undrafted")
	}
	orders := []any{map[string]any{"pawn": map[string]any{"entityId": actorID}, "attack": map[string]any{"entityId": targets[0]}}}
	results, err := na.ApplyCombatOrders(ctx, h, "attack", identity, "combat-attack", orders)
	if err != nil {
		return err
	}
	if err := checkAttackResult(results, actorID, jobDef); err != nil {
		return fmt.Errorf("attack: %w", err)
	}
	attacking, err := read("attack-job", actorID)
	if err != nil {
		return err
	}
	attackingJob, _ := na.AsMap(attacking["job"])
	if na.AsString(attackingJob["defName"]) != jobDef {
		return fmt.Errorf("attack-job: row job %v is not %s", attackingJob["defName"], jobDef)
	}
	// The same key replays the committed receipt without a second order.
	replay, err := na.ApplyCombatOrders(ctx, h, "attack-replay", identity, "combat-attack", orders)
	if err != nil {
		return err
	}
	if !na.DeepEqual(replay, results) {
		return fmt.Errorf("attack-replay returned different results: %#v", replay)
	}
	replayRow, err := read("replay-unchanged", actorID)
	if err != nil {
		return err
	}
	if !na.DeepEqual(replayRow["job"], attacking["job"]) {
		return fmt.Errorf("replay-unchanged: job changed unexpectedly")
	}

	supervisor := &na.ScenarioClock{
		Wire: func(ctx context.Context, label, method string, request map[string]any) (map[string]any, error) {
			return h.Wire(ctx, label, method, request)
		},
		Identity: identity, Owner: na.Controller, Report: report, Grant: grant, CombatTargets: targets,
	}
	rt := &na.ScenarioRuntime{Query: h.Call, Clock: supervisor, Report: report, Tools: names, CombatTargets: targets}

	var victims []map[string]any
	window, ended := 0, false
	for ; window < combatWindows && !ended; window++ {
		if err := supervisor.RenewAuthority(ctx); err != nil {
			return err
		}
		if _, err := na.AdvanceGame(ctx, rt, ticksPerWindow, na.WithTimeout(180*time.Second), na.WithCombatTargets(targets...)); err != nil {
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
		report["last_target"] = victims[0]
		dead, _ := na.AsBool(victims[0]["dead"])
		downed, _ := na.AsBool(victims[0]["downed"])
		ended = dead || downed
	}
	report["combat_windows_used"] = window
	if !ended {
		return fmt.Errorf("combat budget exhausted after %d ticks with the target standing", combatTickBudget)
	}

	// No plan needs the attacker now: the undraft intent releases it.
	if job, err := na.ApplyDraft(ctx, h, "undraft", identity, "combat-undraft", actorID, false); err != nil {
		return err
	} else if issued, _ := na.AsBool(job["issued"]); !issued {
		return fmt.Errorf("undraft: a drafted attacker was not undrafted: %#v", job)
	}
	after, err := read("undrafted-attacker", actorID)
	if err != nil {
		return err
	}
	if drafted, _ := na.AsBool(after["drafted"]); drafted {
		return fmt.Errorf("undrafted-attacker: census shows the attacker drafted")
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
	report["pawn_id"] = actorID
	report["target_ids"] = targets
	report["ticks"] = ticks
	return nil
}

// checkAttackResult asserts the one attack order applied as jobDef.
func checkAttackResult(results []map[string]any, actorID, jobDef string) error {
	if len(results) != 1 {
		return fmt.Errorf("expected one result: %#v", results)
	}
	r := results[0]
	if applied, _ := na.AsBool(r["applied"]); !applied || na.AsString(r["pawnId"]) != actorID || na.AsString(r["jobDef"]) != jobDef {
		return fmt.Errorf("result applied/pawn/jobDef mismatch: %#v", r)
	}
	return nil
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
