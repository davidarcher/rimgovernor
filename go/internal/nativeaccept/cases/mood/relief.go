// The mood/relief case exercises EnsureMood need relief (G01.07e) end to
// end against a live game: a disposable fixture pawn with a genuinely
// deficient need, the GiveJobIntent relieve_need on Actions/Apply (#939, the intent
// bridge's moodReliefAction sends) starting an actual JobGiver_GetJoy job,
// and real game ticks carrying the need back above the native recovery
// threshold. Uses a private disposable fixture (test/mood_setup) since a
// deterministic deficient-need pawn cannot be relied on from native random
// pawn generation and colony state.
package mood

import (
	"context"
	"fmt"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{
		Name: "mood/relief",
		Scope: "EnsureMood relief through the GiveJobIntent relieve_need on Actions/Apply: an actual JobGiver_GetJoy " +
			"job started for a deficient pawn, a resend that applies again without a second job, relief admitted over a " +
			"player-forced current job (#474), a refusal for a pawn in a mental break, and real need recovery observed via native ticks.",
		Start:  cases.LabStart(),
		Keep:   []string{string(na.NeedJoy), "Mood"},
		Budget: 5 * time.Minute,
		Run:    run,
	})
}

func run(ctx context.Context, s cases.Session) error {
	// Joy and Mood stay live: the fixture seeds a joy deficit and the
	// assertion is that native recreation recovers it.
	report := s.Report()
	h := s.Harness()
	identity := s.Identity()
	if _, err := na.GrantAuto(ctx, h.WireFunc(), "acquire", identity); err != nil {
		return err
	}

	pawnRow := func(label, pawnID string) (map[string]any, error) {
		reply, err := h.Wire(ctx, label, "observations_list_pawns", map[string]any{
			"scope":   map[string]any{"expectedIdentity": identity},
			"filter":  map[string]any{"ids": []string{pawnID}, "includeDead": true},
			"details": map[string]any{"needs": true},
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
	joy := func(row map[string]any) float64 {
		needs, _ := na.AsMap(row["needs"])
		return na.AsNumber(needs["joy"])
	}
	setup := func(label, scenario string) (string, error) {
		prepared, err := h.Call(ctx, label, "test/mood_setup", map[string]any{"scenario": scenario})
		if err != nil {
			return "", err
		}
		if success, _ := na.AsBool(prepared["success"]); !success {
			return "", fmt.Errorf("%s: mood_setup refused: %#v", label, prepared)
		}
		pawnID := na.AsString(prepared["pawn"])
		if pawnID == "" {
			return "", fmt.Errorf("%s: mood_setup: missing fixture pawn id: %#v", label, prepared)
		}
		return pawnID, nil
	}
	relieve := func(label, pawnID string) (map[string]any, error) {
		return na.ApplyOne(ctx, h, label, identity, label, map[string]any{"giveJob": map[string]any{"pawn": map[string]any{"id": pawnID}, "options": map[string]any{"relieveNeed": "NEED_JOY"}}})
	}
	issued := func(label, pawnID string, result map[string]any, want bool) error {
		job, err := na.AppliedJob(label, result)
		if err != nil {
			return err
		}
		if got, _ := na.AsBool(job["issued"]); got != want || na.AsString(job["pawnId"]) != pawnID {
			return fmt.Errorf("%s: expected issued=%v for %s, got %#v", label, want, pawnID, job)
		}
		return nil
	}

	// --- Positive path: joy relief ---
	pawnID, err := setup("setup-joy", "joy")
	if err != nil {
		return err
	}
	report["fixture_pawn"] = pawnID
	before, err := pawnRow("target-before", pawnID)
	if err != nil {
		return err
	}
	report["needs_joy_before"] = joy(before)
	if joy(before) >= 0.5 {
		return fmt.Errorf("target-before: expected a deficient joy need, got %v", joy(before))
	}
	result, err := relieve("mood-relief-joy", pawnID)
	if err != nil {
		return err
	}
	if err := issued("mood-relief-joy", pawnID, result, true); err != nil {
		return err
	}
	// A resend while the recreation job runs applies again without a second job.
	again, err := relieve("mood-relief-joy-again", pawnID)
	if err != nil {
		return err
	}
	if err := issued("mood-relief-joy-again", pawnID, again, false); err != nil {
		return err
	}

	// Observe: run real game time forward until the joy need is actually
	// observed at native's own 0.5 recovery threshold; the applied job alone
	// never proves recovery.
	var after map[string]any
	if _, err := na.RunUntil(ctx, h, "observe-joy", 2*na.TicksPerDay, na.Wait{Stall: na.StallBudget()}, func(ctx context.Context) (string, bool, error) {
		row, err := pawnRow("observe-joy", pawnID)
		if err != nil {
			return "", false, err
		}
		after = row
		return fmt.Sprintf("%.2f", joy(row)), joy(row) >= 0.5, nil
	}); err != nil {
		return err
	}
	report["needs_joy_after"] = joy(after)

	// --- Ordered work: a player-forced current job is interruption evidence,
	// not an eligibility veto (#474), so relief is applied over it. ---
	forcedPawnID, err := setup("setup-forced", "forced")
	if err != nil {
		return err
	}
	forcedRow, err := pawnRow("target-forced", forcedPawnID)
	if err != nil {
		return err
	}
	forcedJob, _ := na.AsMap(forcedRow["job"])
	if playerForced, _ := na.AsBool(forcedJob["playerForced"]); !playerForced {
		return fmt.Errorf("target-forced: expected a player-forced current job, got %#v", forcedJob)
	}
	forced, err := relieve("mood-relief-forced", forcedPawnID)
	if err != nil {
		return err
	}
	if err := issued("mood-relief-forced", forcedPawnID, forced, true); err != nil {
		return err
	}
	report["ordered_work_pawn"] = forcedPawnID

	// --- Negative: a pawn in a mental break is refused. ---
	mentalPawnID, err := setup("setup-mental", "mental")
	if err != nil {
		return err
	}
	mental, err := relieve("mood-relief-mental", mentalPawnID)
	if err != nil {
		return err
	}
	if err := na.Refused("mood-relief-mental", mental, "mental break"); err != nil {
		return err
	}
	report["negative_mental_pawn"] = mentalPawnID

	return nil
}
