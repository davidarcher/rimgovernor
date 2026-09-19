// The mood/provision case proves the MaintainMood provisioning vertical
// (#255) against a live game: a pawn whose thought pressure is dominated
// by removable environment thoughts (SleptOutside + NeedJoy) is read
// through the same social block the routine census requests, the mood
// review reduces that pressure to the upkeep goals whose facilities remove
// it (EnsureInitialShelter, EnsureBasicComfort/EnsureComfort) and proposes deferring to them
// instead of a native relief job, and both thoughts then clear within a
// day of ordinary native ticks with no relief dispatched at all. The
// owner goals' own planning (a roofed bed, a recreation source) is covered
// by shelter/... and facility/comfort; this case owns the observation ->
// review -> proposal contract and the no-relief clearing.
package mood

import (
	"context"
	"encoding/json"
	"fmt"
	"os"
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
	cases.Register(cases.Case{
		Name: "mood/provision",
		Scope: "MaintainMood facility provisioning (#255): dominant SleptOutside + NeedJoy pressure read from the " +
			"routine social block defers to EnsureInitialShelter/EnsureComfort instead of a relief job, and both " +
			"thoughts clear within a day without any relief dispatched.",
		Start:  cases.DebugStart{},
		Keep:   []string{string(na.NeedJoy), "Mood"},
		Budget: 5 * time.Minute,
		Run:    runProvision,
	})
}

// provisionRow reads the fixture pawn with the routine census's detail
// selection (needs, schedule, social) and decodes it as the bridge would.
func provisionRow(ctx context.Context, h *na.Harness, identity map[string]any, label, pawnID string) (*o.PawnState, error) {
	reply, err := h.Wire(ctx, label, "observations_list_pawns", map[string]any{
		"scope":   map[string]any{"expectedIdentity": identity},
		"filter":  map[string]any{"ids": []string{pawnID}, "includeDead": true},
		"details": map[string]any{"needs": true, "schedule": true, "social": true},
		"page":    map[string]any{"limit": 1},
	})
	if err != nil {
		return nil, err
	}
	encoded, err := json.Marshal(reply)
	if err != nil {
		return nil, err
	}
	typed := &o.ListPawnsReply{}
	if err = protojson.Unmarshal(encoded, typed); err != nil {
		return nil, fmt.Errorf("%s: decode pawn reply: %w", label, err)
	}
	observed := typed.GetObserved()
	if observed == nil || len(observed.Pawns) != 1 || observed.Pawns[0].GetPawn().GetId() != pawnID {
		return nil, fmt.Errorf("%s: expected exactly the fixture pawn, got %#v", label, reply)
	}
	return observed.Pawns[0], nil
}

// provisionPawn lifts the row the way observation.routineMood does for the
// fields this case reviews.
func provisionPawn(row *o.PawnState, pawnID string) policy.MoodPawn {
	p := policy.MoodPawn{ID: policy.PawnID(pawnID), Dead: domain.Known(row.GetDead()), Downed: domain.Known(row.GetDowned()), Drafted: domain.Known(row.GetDrafted()), Mental: domain.Known(row.MentalState != nil)}
	if job := row.Job; job != nil {
		p.PlayerForced = domain.Known(job.GetPlayerForced())
	}
	if n := row.Needs; n != nil {
		p.Mood, p.Threshold = domain.Known(n.GetMood()), domain.Known(n.GetBreakThresholdMinor())
		p.Food, p.Rest, p.Joy = domain.Known(n.GetFood()), domain.Known(n.GetRest()), domain.Known(n.GetJoy())
	}
	p.Thoughts = observation.MoodThoughts(row)
	return p
}

func provisionThought(p policy.MoodPawn, def string) (float64, bool) {
	rows, known := p.Thoughts.Value()
	if !known {
		return 0, false
	}
	for _, t := range rows {
		if t.Def == def {
			return t.Offset, true
		}
	}
	return 0, false
}

func runProvision(ctx context.Context, s cases.Session) error {
	report := s.Report()
	h := s.Harness()
	identity := s.Identity()
	if !na.Contains(s.Names(), "rimgovernor/observations_list_pawns") {
		return fmt.Errorf("missing rimgovernor/observations_list_pawns in discovery")
	}
	prepared, err := h.Call(ctx, "setup-environment", "test/mood_setup", map[string]any{"scenario": "environment"})
	if err != nil {
		return err
	}
	if success, _ := na.AsBool(prepared["success"]); !success {
		return fmt.Errorf("setup-environment: mood_setup refused: %#v", prepared)
	}
	pawnID := na.AsString(prepared["pawn"])
	if pawnID == "" {
		return fmt.Errorf("setup-environment: missing fixture pawn id: %#v", prepared)
	}
	report["fixture_pawn"] = pawnID

	row, err := provisionRow(ctx, h, identity, "target-before", pawnID)
	if err != nil {
		return err
	}
	before := provisionPawn(row, pawnID)
	thoughts, known := before.Thoughts.Value()
	if !known {
		return fmt.Errorf("target-before: social block left the thought pressure unknown: %v", row.GetSocial())
	}
	report["thoughts_before"] = thoughts
	for _, def := range []string{"SleptOutside", "NeedJoy"} {
		if _, ok := provisionThought(before, def); !ok {
			return fmt.Errorf("target-before: fixture pressure %s not observed in %v", def, thoughts)
		}
	}
	history, err := policy.ReviewMood(domain.Known([]policy.MoodPawn{before}), policy.MoodHistory{})
	if err != nil {
		return err
	}
	if len(history.States) != 1 || !history.States[0].Active {
		return fmt.Errorf("target-before: fixture mood pressure did not open an active mood state: %+v", history)
	}
	state := history.States[0]
	report["provision_before"] = state.Provision
	owners := map[policy.GoalID]bool{}
	for _, p := range state.Provision {
		owners[p.Goal] = true
	}
	if !owners[policy.EnsureBasicComfort] || !owners[policy.EnsureComfort] || !owners[policy.EnsureInitialShelter] {
		return fmt.Errorf("target-before: expected EnsureBasicComfort, EnsureComfort and EnsureInitialShelter provisioning, got %+v", state.Provision)
	}
	proposal, err := policy.SelectMoodMethod(state, nil)
	if err != nil {
		return err
	}
	report["proposal_before"] = proposal
	if proposal.Reason != policy.MoodProvisioned || !owners[proposal.Goal] {
		return fmt.Errorf("target-before: expected a facility_provision proposal deferring to an owner goal, got %+v", proposal)
	}

	// Ordinary native ticks, no relief dispatched: the SleptOutside memory
	// ages out and the pawn recreates at the fixture's horseshoes pin on its
	// own, so the NeedJoy stage lifts.
	var after policy.MoodPawn
	elapsed, err := na.RunUntil(ctx, h, "clear", na.TicksPerDay+na.TicksPerDay/4, na.Wait{Stall: na.StallBudget()}, func(ctx context.Context) (string, bool, error) {
		row, err := provisionRow(ctx, h, identity, "clear-probe", pawnID)
		if err != nil {
			return "", false, err
		}
		after = provisionPawn(row, pawnID)
		_, slept := provisionThought(after, "SleptOutside")
		_, joy := provisionThought(after, "NeedJoy")
		return na.Signature(slept, joy), !slept && !joy, nil
	})
	report["clear_ticks"] = elapsed
	if err != nil {
		return fmt.Errorf("clear: %w", err)
	}
	thoughts, _ = after.Thoughts.Value()
	report["thoughts_after"] = thoughts
	history, err = policy.ReviewMood(domain.Known([]policy.MoodPawn{after}), history)
	if err != nil {
		return err
	}
	if len(history.States) != 1 || len(history.States[0].Provision) != 0 {
		return fmt.Errorf("clear: cleared pressure still provisions: %+v", history.States)
	}
	proposal, err = policy.SelectMoodMethod(history.States[0], nil)
	if err != nil {
		return err
	}
	report["proposal_after"] = proposal
	if proposal.Reason == policy.MoodRelief || proposal.Reason == policy.MoodProvisioned {
		return fmt.Errorf("clear: pressure cleared but the review still proposes %+v", proposal)
	}

	logData, err := os.ReadFile(s.Config().StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), s.Config().Headless)
}
