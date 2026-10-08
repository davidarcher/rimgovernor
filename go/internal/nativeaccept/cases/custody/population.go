// The custody/population case exercises population custody (G01.07e, issue
// #27): the Capture and Rescue GiveJobIntents on Actions/Apply (#939), the
// same intents bridge's captureAction and rescueAction send. A downed
// hostile is actually captured into prisoner custody and a downed,
// unadmitted friendly guest is actually rescued into an ordinary bed --
// real native roster/status change observed via ticks, not just a receipt.
// Between the two, the captured prisoner takes PrisonerInteractionIntents
// on Actions/Apply (issues #95, #941): the ReduceResistance and
// AttemptRecruit modes apply and read back, and once the fixture recruits
// the prisoner a further intent is refused against live custody state.
// Uses the disposable test/population_setup fixture (PopulationFixture.cs)
// since a deterministic downed candidate and guest near a ready prison and
// spare beds cannot be relied on from native random pawn generation and
// colony state.
package custody

import (
	"context"
	"fmt"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{
		Name: "custody/population",
		Scope: "Population custody through GiveJobIntents on Actions/Apply: an actual Capture job carries a downed hostile " +
			"into prisoner custody and an actual Rescue job carries a downed unadmitted guest into an ordinary bed, " +
			"with live refusals (capture of a non-hostile guest, a missing patient), a resend that applies again without " +
			"a second job, and real roster/status change observed via native ticks. The captured prisoner then " +
			"takes ReduceResistance and AttemptRecruit interaction intents on Actions/Apply that read back, and a " +
			"further intent is refused once a native recruit has taken the pawn out of custody.",
		Start:  cases.Fixture{Op: "test/population_setup", Args: map[string]any{"candidateKind": "Villager"}, On: cases.LabStart()},
		Budget: cases.LabBudget,
		Crew:   cases.Crew{Size: 3}, Run: run,
	})
}

func run(ctx context.Context, s cases.Session) error {
	// --- Fixture: one disposable downed hostile candidate (capture target,
	// a ready prisoner bed) and one downed unadmitted friendly guest (rescue
	// target, spare sleeping spots). The fixture ran before authority is
	// acquired: its direct spawns would revoke a held grant.
	report := s.Report()
	h := s.Harness()
	identity := s.Identity()
	prepared := s.Prepared()
	candidateID := na.AsString(prepared["candidate"])
	visitorID := na.AsString(prepared["visitor"])
	prisonID := na.AsString(prepared["prison"])
	if candidateID == "" || visitorID == "" || prisonID == "" {
		return fmt.Errorf("setup: missing fixture ids: %#v", prepared)
	}
	report["fixture_candidate"] = candidateID
	report["fixture_visitor"] = visitorID

	bedChecks := na.AsSlice(prepared["bedChecks"])
	var capturerID string
	for _, entry := range bedChecks {
		row, _ := na.AsMap(entry)
		usable, _ := na.AsBool(row["usable"])
		reachable, _ := na.AsBool(row["reachable"])
		reservable, _ := na.AsBool(row["reservable"])
		if usable && reachable && reservable && na.AsString(row["nativeBed"]) == prisonID {
			capturerID = na.AsString(row["pawn"])
			break
		}
	}
	if capturerID == "" {
		return fmt.Errorf("setup: no fixture worker can reach/reserve the prisoner bed: %#v", bedChecks)
	}
	report["fixture_capturer"] = capturerID

	// acquire (re)issues SetMode(Auto) at the freshest native generation.
	// Native simulation outside an owned scope revokes authority, so this is
	// called again after any such stretch.
	acquire := func(label string) error {
		_, err := na.GrantAuto(ctx, h.WireFunc(), label, identity)
		return err
	}
	if err := acquire("acquire"); err != nil {
		return err
	}

	pawnRow := func(label, pawnID string) (map[string]any, error) {
		reply, err := h.Wire(ctx, label, "observations_list_pawns", map[string]any{
			"scope":   map[string]any{"expectedIdentity": identity},
			"filter":  map[string]any{"ids": []string{pawnID}, "includeDead": true},
			"details": map[string]any{},
		})
		if err != nil {
			return nil, err
		}
		_, observed, err := na.Outcome(reply, "observed")
		if err != nil {
			return nil, err
		}
		rows := na.AsSlice(observed["pawns"])
		if len(rows) != 1 {
			return nil, fmt.Errorf("%s: expected exactly one observed pawn, got %#v", label, observed)
		}
		row, _ := na.AsMap(rows[0])
		pawn, _ := na.AsMap(row["pawn"])
		if na.AsString(pawn["id"]) != pawnID {
			return nil, fmt.Errorf("%s: unexpected pawn row: %#v", label, row)
		}
		return row, nil
	}
	order := func(targetID, kind string) map[string]any {
		return na.GiveJob(capturerID, kind, targetID)
	}
	// ordered applies one custody intent, checks it issued jobDef, then
	// resends it and checks the resend applies without a second job.
	ordered := func(label, targetID, kind, jobDef string) error {
		result, err := na.ApplyOne(ctx, h, "apply-"+label, identity, label, order(targetID, kind))
		if err != nil {
			return err
		}
		job, err := na.AppliedJob(label, result)
		if err != nil {
			return err
		}
		if issued, _ := na.AsBool(job["issued"]); !issued || na.AsString(job["pawnId"]) != capturerID || na.AsString(job["jobDef"]) != jobDef {
			return fmt.Errorf("%s: expected an issued %s job, got %#v", label, jobDef, job)
		}
		again, err := na.ApplyOne(ctx, h, "resend-"+label, identity, label+"-again", order(targetID, kind))
		if err != nil {
			return err
		}
		if job, err := na.AppliedJob(label+" resend", again); err != nil {
			return err
		} else if issued, _ := na.AsBool(job["issued"]); issued {
			return fmt.Errorf("%s resend issued a second job: %#v", label, again)
		}
		return nil
	}
	// until runs game time until the pawn's row satisfies done.
	until := func(label, pawnID string, done func(map[string]any) bool) error {
		_, err := na.RunUntil(ctx, h, label, 2*na.TicksPerDay, na.Wait{Stall: na.StallBudget()}, func(ctx context.Context) (string, bool, error) {
			row, err := pawnRow(label, pawnID)
			if err != nil {
				return "", false, err
			}
			return fmt.Sprint(row["prisoner"], row["inBed"], row["downed"]), done(row), nil
		})
		return err
	}

	// =================================================================
	// Capture: the downed hostile candidate.
	// =================================================================

	candidateRow, err := pawnRow("candidate-before", candidateID)
	if err != nil {
		return err
	}
	if downed, _ := na.AsBool(candidateRow["downed"]); !downed {
		return fmt.Errorf("candidate-before: expected the fixture candidate to be downed: %#v", candidateRow)
	}
	if prisoner, _ := na.AsBool(candidateRow["prisoner"]); prisoner {
		return fmt.Errorf("candidate-before: expected the fixture candidate not yet a prisoner: %#v", candidateRow)
	}

	// Refusals, checked live at apply: a non-hostile guest cannot be
	// captured, and an absent patient is not found.
	friendly, err := na.ApplyOne(ctx, h, "apply-capture-guest", identity, "custody-capture-guest", order(visitorID, "Capture"))
	if err != nil {
		return err
	}
	if err := na.Refused("capture-guest", friendly, "Capture refused"); err != nil {
		return err
	}
	missing, err := na.ApplyOne(ctx, h, "apply-capture-missing", identity, "custody-capture-missing", order("Thing_NoSuchPatient", "Capture"))
	if err != nil {
		return err
	}
	if refusal, ok := na.AsMap(missing["refused"]); !ok || na.AsString(refusal["code"]) != "FAILURE_CODE_NOT_FOUND" {
		return fmt.Errorf("apply-capture-missing: expected a NOT_FOUND refusal, got %#v", missing)
	}

	if err := ordered("custody-capture", candidateID, "Capture", "Capture"); err != nil {
		return err
	}
	// Observe: run real game time forward until the candidate is actually a
	// colony prisoner; the applied order alone never proves capture.
	if err := until("observe-capture", candidateID, func(row map[string]any) bool {
		prisoner, _ := na.AsBool(row["prisoner"])
		return prisoner
	}); err != nil {
		return err
	}
	report["candidate_captured"] = true

	// =================================================================
	// Prisoner interaction: the captured candidate (issue #95).
	// =================================================================

	if err := acquire("re-acquire-before-interaction"); err != nil {
		return err
	}
	// personRow reads the prisoner through rimgovernor/observations_read_population,
	// the same read bridge.ReadRoundsPopulation decodes the current
	// interaction from.
	personRow := func(label string) (map[string]any, []string, error) {
		reply, err := h.Wire(ctx, label, "observations_read_population", map[string]any{"scope": map[string]any{"expectedIdentity": identity}})
		if err != nil {
			return nil, nil, err
		}
		_, observed, err := na.Outcome(reply, "observed")
		if err != nil {
			return nil, nil, err
		}
		var supported []string
		for _, entry := range na.AsSlice(observed["supportedInteractions"]) {
			def, _ := na.AsMap(entry)
			supported = append(supported, na.AsString(def["defName"]))
		}
		for _, entry := range na.AsSlice(observed["persons"]) {
			person, _ := na.AsMap(entry)
			if na.PawnRef(person) == candidateID {
				// The custody facts ride the pawn table row (#1343).
				return person, supported, h.JoinPawn(ctx, label, identity, person)
			}
		}
		return nil, supported, fmt.Errorf("%s: candidate %s missing from the population census", label, candidateID)
	}
	prisonerBefore, supported, err := personRow("prisoner-before")
	if err != nil {
		return err
	}
	report["supported_interactions"] = supported
	for _, core := range []string{"AttemptRecruit", "MaintainOnly", "ReduceResistance", "Release"} {
		if !na.Contains(supported, core) {
			return fmt.Errorf("prisoner-before: Core interaction %s missing from supportedInteractions %v", core, supported)
		}
	}
	prisonerPawn, _ := na.AsMap(prisonerBefore["pawn"])
	if prisoner, _ := na.AsBool(prisonerPawn["prisoner"]); !prisoner {
		return fmt.Errorf("prisoner-before: expected the captured candidate to be a prisoner: %#v", prisonerBefore)
	}
	// The routine release path's facts (#236): a current prisoner carries
	// native's recruit resistance and the TimeAsPrisoner record, which
	// ProtoJSON renders as a decimal string for int64.
	if _, ok := prisonerBefore["resistance"]; !ok {
		return fmt.Errorf("prisoner-before: expected resistance on the captured prisoner: %#v", prisonerBefore)
	}
	if _, ok := prisonerBefore["prisonerTicks"]; !ok {
		return fmt.Errorf("prisoner-before: expected prisonerTicks on the captured prisoner: %#v", prisonerBefore)
	}
	report["prisoner_ticks"] = prisonerBefore["prisonerTicks"]
	// applyInteraction sends one PrisonerInteractionIntent on Actions/Apply
	// (#941) and returns the action's result: applied carries the prisoner
	// evidence, refused the native reason.
	applyInteraction := func(label, key, pawn, mode string) (map[string]any, error) {
		reply, err := h.Wire(ctx, label, "operations_apply", map[string]any{"identity": identity, "actions": []any{map[string]any{
			"key": key, "prisoner": map[string]any{"pawnId": pawn, "interaction": mode},
		}}})
		if err != nil {
			return nil, err
		}
		results := na.AsSlice(reply["results"])
		if len(results) != 1 {
			return nil, fmt.Errorf("%s: expected one result, got %#v", label, reply)
		}
		result, _ := na.AsMap(results[0])
		return result, nil
	}
	appliedInteraction := func(label, key, mode, def string) error {
		result, err := applyInteraction(label, key, candidateID, mode)
		if err != nil {
			return err
		}
		receipt, _ := na.AsMap(result["applied"])
		applied, _ := na.AsMap(receipt["applied"])
		observed, _ := na.AsMap(applied["observed"])
		effect, ok := na.AsMap(observed["prisoner"])
		if !ok || na.AsString(effect["outcome"]) != "held" || na.AsString(effect["interactionDef"]) != def {
			return fmt.Errorf("%s: expected applied held %s evidence, got %#v", label, def, result)
		}
		return nil
	}

	// Refusal: an absent prisoner is refused at apply time, before any write.
	missing, err = applyInteraction("apply-interaction-missing", "prisoner-interaction-missing", "Thing_NoSuchPrisoner", "PRISONER_INTERACTION_REDUCE_RESISTANCE")
	if err != nil {
		return err
	}
	if refusal, ok := na.AsMap(missing["refused"]); !ok || na.AsString(refusal["code"]) != "FAILURE_CODE_NOT_FOUND" {
		return fmt.Errorf("apply-interaction-missing: expected a NOT_FOUND refusal, got %#v", missing)
	}

	// ReduceResistance: a Core mode beyond the routine Recruit/Maintain pair.
	// Resending the same intent applies again as it stands.
	for _, label := range []string{"apply-reduce-resistance", "resend-reduce-resistance"} {
		if err := appliedInteraction(label, "prisoner-interaction-reduce", "PRISONER_INTERACTION_REDUCE_RESISTANCE", "ReduceResistance"); err != nil {
			return err
		}
	}
	afterReduce, _, err := personRow("prisoner-after-reduce")
	if err != nil {
		return err
	}
	if na.AsString(afterReduce["interaction"]) != "ReduceResistance" {
		return fmt.Errorf("prisoner-after-reduce: expected the native interaction to read back ReduceResistance, got %#v", afterReduce)
	}
	report["reduce_resistance_held"] = true

	if err := appliedInteraction("apply-recruit", "prisoner-interaction-recruit", "PRISONER_INTERACTION_ATTEMPT_RECRUIT", "AttemptRecruit"); err != nil {
		return err
	}
	afterSet, _, err := personRow("prisoner-after-recruit-order")
	if err != nil {
		return err
	}
	if na.AsString(afterSet["interaction"]) != "AttemptRecruit" {
		return fmt.Errorf("prisoner-after-recruit-order: expected AttemptRecruit, got %#v", afterSet)
	}

	// Actual outcome: the fixture recruits through the native recruit-success
	// path; the census reports the admitted colonist, and a further
	// interaction intent is refused because the pawn is no longer a prisoner.
	recruited, err := h.Call(ctx, "fixture-recruit", "test/population_recruit", map[string]any{})
	if err != nil {
		return err
	}
	if success, _ := na.AsBool(recruited["success"]); !success {
		return fmt.Errorf("fixture-recruit: population_recruit refused: %#v", recruited)
	}
	afterRecruit, _, err := personRow("prisoner-after-recruit")
	if err != nil {
		return err
	}
	afterRecruitPawn, _ := na.AsMap(afterRecruit["pawn"])
	if prisoner, _ := na.AsBool(afterRecruitPawn["prisoner"]); prisoner {
		return fmt.Errorf("prisoner-after-recruit: expected the candidate to have left prisoner custody: %#v", afterRecruit)
	}
	if admitted, _ := na.AsBool(afterRecruit["admitted"]); !admitted {
		return fmt.Errorf("prisoner-after-recruit: expected the candidate to be an admitted colonist: %#v", afterRecruit)
	}
	gone, err := applyInteraction("apply-after-recruit", "prisoner-interaction-after-recruit", candidateID, "PRISONER_INTERACTION_MAINTAIN_ONLY")
	if err != nil {
		return err
	}
	if _, ok := na.AsMap(gone["refused"]); !ok {
		return fmt.Errorf("apply-after-recruit: expected a refusal for a recruited colonist, got %#v", gone)
	}
	report["candidate_recruited"] = true

	// =================================================================
	// Rescue: the downed, unadmitted friendly guest.
	// =================================================================

	// Re-acquire: the Fast-speed run to observe the capture complete let the
	// colony's own AI issue jobs and run its usual simulation outside this
	// tool's authority.Owned() scope, which revokes whatever lease was held
	// (see the acquire closure's comment above). The prior lease is gone by
	// now; a fresh one is required before the real rescue dispatch below.
	if err := acquire("re-acquire-before-rescue"); err != nil {
		return err
	}
	visitorRowBefore, err := pawnRow("visitor-before", visitorID)
	if err != nil {
		return err
	}
	if downed, _ := na.AsBool(visitorRowBefore["downed"]); !downed {
		return fmt.Errorf("visitor-before: expected the fixture guest to be downed: %#v", visitorRowBefore)
	}
	if inBed, _ := na.AsBool(visitorRowBefore["inBed"]); inBed {
		return fmt.Errorf("visitor-before: expected the fixture guest not yet admitted to a bed: %#v", visitorRowBefore)
	}
	if err := ordered("custody-rescue", visitorID, "Rescue", "Rescue"); err != nil {
		return err
	}
	if err := until("observe-rescue", visitorID, func(row map[string]any) bool {
		inBed, _ := na.AsBool(row["inBed"])
		return inBed
	}); err != nil {
		return err
	}
	report["visitor_rescued"] = true

	return nil
}
