// The quest/joiner case proves MaintainPopulation's joiner answer (#250)
// end to end: a real ThreatReward_Raid_Joiner offer generated through the
// native storyteller path reads back in the quest census with its root
// script_def, stays unanswered while the colony has declared no population
// capacity, and once the player declares a population policy the colony can
// meet (spare unowned beds and a food runway past the policy reserve, both
// staged by the fixture) the routine-population-joiner planner accepts it
// through the QuestAccept vertical, the joiner walks into the colony and
// the native quest reports itself accepted and ongoing.
package quest

import (
	"context"
	"errors"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

const (
	joinerPrepareTool = "test/joiner_quest_prepare"
	joinerReadTool    = "test/joiner_quest_read"
	joinerBaseline    = "RimGovernor-tribal8-baseline"
	// joinerPolicyMaximum leaves room for one more colonist on the eight-
	// colonist baseline; joinerPolicyFoodDays is the smallest reserve the
	// policy admits, which the fixture's survival meals exceed many times.
	joinerPolicyMaximum  = 20
	joinerPolicyFoodDays = 1.0
	// joinerCeiling bounds each service-side wait; the stall budget ends it
	// earlier when nothing moves.
	joinerCeiling = 8 * time.Minute
	// joinerArrivalTicks bounds the post-acceptance run for the joiner:
	// ThreatReward_Raid_Joiner walks them in 600..1200 ticks after the
	// accept, well before its raid (a further 1800..2400 ticks).
	joinerArrivalTicks = 3000
)

func init() {
	cases.Register(cases.Case{
		Name: "quest/joiner",
		Scope: "Issue #250: a real ThreatReward_Raid_Joiner offer reads back in the quest census with its root " +
			"script_def, is left unanswered without a declared population policy, and once the player declares one the " +
			"colony can meet (headroom, food reserve, a spare unowned bed) MaintainPopulation accepts it through the " +
			"QuestAccept vertical and the joiner arrives natively.",
		Start: cases.Fixture{Op: joinerPrepareTool, On: cases.Save{Name: joinerBaseline}},
		// The building families keep supervised windows running; the joiner
		// planner alone never advances the clock.
		Serve: &cases.ServeSpec{
			Families: []string{"population-joiner", "supply", "shelter"}, NativeTimeout: 15 * time.Second, Prefix: "quest-joiner",
		},
		Budget: 15 * time.Minute,
		Run:    runJoiner,
	})
}

// joinerAnswer is what the durable journal proves for the offer: the
// MaintainPopulation method whose plan carries a QuestAccept for it.
type joinerAnswer struct {
	Goal   domain.GoalID `json:"goal"`
	Epoch  uint64        `json:"epoch"`
	Plan   domain.PlanID `json:"plan"`
	Reward int32         `json:"reward_choice"`
	Stage  string        `json:"stage"`
}

// joinerAnswers lists every QuestAccept plan MaintainPopulation has ever
// admitted, across epochs, keyed by the accepted quest.
func joinerAnswers(ctx context.Context, st *store.Store) (map[domain.QuestID]joinerAnswer, bool, error) {
	review, err := st.LoadRoutineReview(ctx)
	if err != nil {
		return nil, false, err
	}
	out := map[domain.QuestID]joinerAnswer{}
	found := false
	for _, binding := range review.Goals {
		if binding.Need != policy.MaintainPopulation {
			continue
		}
		found = true
		g, err := st.LoadGoal(ctx, binding.Goal)
		if err != nil {
			return nil, false, err
		}
		for epoch := uint64(0); epoch <= g.Goal.Epoch; epoch++ {
			methods, err := st.LoadGoalMethods(ctx, binding.Goal, epoch)
			if err != nil && !errors.Is(err, store.ErrNotFound) {
				return nil, false, err
			}
			for _, m := range methods {
				plan, err := st.LoadPlan(ctx, m.Plan)
				if err != nil {
					return nil, false, err
				}
				for i, action := range plan.Spec.Actions() {
					accept, ok := action.QuestAccept()
					if !ok {
						continue
					}
					out[accept.Quest()] = joinerAnswer{Goal: binding.Goal, Epoch: epoch, Plan: m.Plan, Reward: accept.RewardChoice(), Stage: string(plan.Progress[i].View().Stage)}
				}
			}
		}
	}
	return out, found, nil
}

func runJoiner(ctx context.Context, s cases.Session) error {
	report, h, identity, prepared := s.Report(), s.Harness(), s.Identity(), s.Prepared()
	if !na.Contains(s.Names(), joinerReadTool) {
		return fmt.Errorf("missing %s in discovery; rebuild the native mod with -Fixture QuestAcceptFixture", joinerReadTool)
	}
	questID := domain.QuestID(na.AsString(prepared["questId"]))
	if questID == "" || na.AsString(prepared["scriptDef"]) != "ThreatReward_Raid_Joiner" || na.AsString(prepared["state"]) != "NotYetAccepted" {
		return fmt.Errorf("fixture: unexpected joiner offer: %#v", prepared)
	}
	if beds := na.AsSlice(prepared["beds"]); len(beds) == 0 {
		return fmt.Errorf("fixture: no spare bed spawned: %#v", prepared)
	}
	before := na.AsSlice(prepared["colonists"])
	if len(before) == 0 {
		return fmt.Errorf("fixture: no colonists: %#v", prepared)
	}
	report["fixture_quest"] = string(questID)
	report["colonists_before"] = len(before)

	// The baseline's naming dialog goes first, so the service meets no
	// force-pausing window of its own.
	if _, err := na.ConfirmColonyNames(ctx, h, report); err != nil {
		return err
	}
	// The census the service's own review will read must already carry the
	// offer with its root script name, exactly as bridge.QuestOffer decodes
	// it (observations_read_world_progression's quests page).
	reply, err := h.Wire(ctx, "census-before", "observations_read_world_progression", map[string]any{
		"scope": map[string]any{"expectedIdentity": identity}, "includeStorage": false, "page": map[string]any{"limit": 64},
	})
	if err != nil {
		return err
	}
	_, observed, err := na.Outcome(reply, "observed")
	if err != nil {
		return err
	}
	var row map[string]any
	for _, raw := range na.AsSlice(observed["quests"]) {
		r, _ := na.AsMap(raw)
		if na.AsString(r["id"]) == string(questID) {
			row = r
		}
	}
	if row == nil {
		return fmt.Errorf("census-before: the joiner offer is missing from the quest census: %#v", observed["quests"])
	}
	report["census_before"] = row
	if na.AsString(row["scriptDef"]) != "ThreatReward_Raid_Joiner" {
		return fmt.Errorf("census-before: quest row lacks its root script_def: %#v", row)
	}
	if state := na.AsString(row["state"]); state != "QUEST_STATE_NOT_YET_ACCEPTED" && state != "NotYetAccepted" {
		return fmt.Errorf("census-before: expected a not-yet-accepted offer: %#v", row)
	}
	if canAccept, _ := na.AsBool(row["canAccept"]); !canAccept {
		return fmt.Errorf("census-before: the offer is not acceptable: %#v", row)
	}
	if requires, _ := na.AsBool(row["requiresAccepter"]); requires {
		return fmt.Errorf("census-before: a joiner offer should not name an accepter: %#v", row)
	}

	service, err := s.Serve(ctx, s.Spec())
	if err != nil {
		return err
	}
	if _, err := service.Acquire(); err != nil {
		return err
	}
	service.KeepAuthority(ctx)
	st, err := service.Store(ctx)
	if err != nil {
		return err
	}
	waitFor := func(label string, ready func(map[domain.QuestID]joinerAnswer, bool) (bool, error)) (map[domain.QuestID]joinerAnswer, error) {
		var answers map[domain.QuestID]joinerAnswer
		err := na.WaitProgress(ctx, na.Wait{Ceiling: joinerCeiling, Stall: na.StallBudget(), Terminal: service.Exited}, func(ctx context.Context) (string, bool, error) {
			a, found, err := joinerAnswers(ctx, st)
			if err != nil {
				return "", false, err
			}
			answers = a
			done, err := ready(a, found)
			if err != nil {
				return "", false, err
			}
			return na.Signature(found, fmt.Sprint(a)), done, nil
		})
		report[label+"_answers"] = answers
		if err != nil {
			return answers, fmt.Errorf("%s: %w", label, err)
		}
		return answers, nil
	}

	// Phase 1: without a declared population policy the colony has no
	// capacity, so the first reviews bind no MaintainPopulation deficit for
	// the offer and admit nothing: an offer the colony cannot host is left
	// alone.
	review, diagnostics, err := service.WaitRoutineReview(ctx, st, joinerCeiling)
	report["phase1_review"] = diagnostics
	if err != nil {
		return fmt.Errorf("phase1: %w", err)
	}
	answers, found, err := joinerAnswers(ctx, st)
	if err != nil {
		return err
	}
	report["phase1_answers"] = answers
	report["phase1_population_bound"] = found
	if len(answers) != 0 {
		return fmt.Errorf("phase1: review %d answered the offer (%v) before any population policy was declared", review.Revision, answers)
	}

	// The player declares a policy the colony can meet through the same
	// route the web client uses; the next review reads it as
	// RoutineFacts.PopulationCapacity.
	submission, status, err := service.API("POST", "/api/player/population-policy/replace", map[string]any{
		"requestId": "quest-joiner-policy-1", "expected": identity,
		"policy": map[string]any{"maximum": joinerPolicyMaximum, "foodDays": joinerPolicyFoodDays},
	}, service.Token)
	if err != nil {
		return err
	}
	if status != 201 {
		return fmt.Errorf("population-policy/replace: status=%d body=%#v", status, submission)
	}
	report["population_policy"] = submission

	// Phase 2: MaintainPopulation admits one QuestAccept for the offer and
	// the executor completes it against the live quest.
	answers, err = waitFor("phase2", func(a map[domain.QuestID]joinerAnswer, found bool) (bool, error) {
		for quest, answer := range a {
			if quest != questID {
				return false, fmt.Errorf("MaintainPopulation accepted quest %s, not the joiner offer %s", quest, questID)
			}
			if domain.Stage(answer.Stage) == domain.Unsuccessful || domain.Stage(answer.Stage) == domain.Cancelled {
				return false, fmt.Errorf("the joiner acceptance ended %s", answer.Stage)
			}
		}
		answer, ok := a[questID]
		return ok && domain.Stage(answer.Stage) == domain.Completed, nil
	})
	if err != nil {
		return err
	}
	if answers[questID].Reward != -1 {
		// ThreatReward roots carry a reward-choice part, whose first option
		// the planner takes; -1 means the census reported no choices.
		report["reward_choice"] = answers[questID].Reward
	}
	report["run_keepalive"] = service.Stop()

	// Independent native read after the service releases the game slot:
	// the quest is accepted and ongoing, and running the game on lets the
	// quest's own parts walk the joiner in as a free colonist.
	h, err = s.Reattach(ctx)
	if err != nil {
		return fmt.Errorf("reopen session after service stop: %w", err)
	}
	if _, err := h.Call(ctx, "pause-after", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	after, err := h.Call(ctx, "quest-after", joinerReadTool, map[string]any{"questId": string(questID)})
	if err != nil {
		return err
	}
	report["quest_after"] = after
	if found, _ := na.AsBool(after["questFound"]); !found {
		return fmt.Errorf("quest-after: the joiner quest is gone: %#v", after)
	}
	if state := na.AsString(after["state"]); state == "NotYetAccepted" {
		return fmt.Errorf("quest-after: the joiner quest was never accepted natively: %#v", after)
	}
	if tick := int64(na.AsNumber(after["acceptedTick"])); tick < 0 {
		return fmt.Errorf("quest-after: no native acceptance tick: %#v", after)
	}
	var arrived map[string]any
	elapsed, err := na.RunUntil(ctx, h, "joiner-arrival", joinerArrivalTicks, na.Wait{Stall: na.StallBudget()}, func(ctx context.Context) (string, bool, error) {
		read, err := h.Call(ctx, "joiner-read", joinerReadTool, map[string]any{"questId": string(questID)})
		if err != nil {
			return "", false, err
		}
		arrived = read
		if state := na.AsString(read["state"]); state == "EndedFailed" || state == "EndedInvalid" {
			return "", false, fmt.Errorf("joiner-arrival: the quest ended %s: %#v", state, read)
		}
		count := len(na.AsSlice(read["colonists"]))
		return na.Signature(count, na.AsString(read["state"])), count > len(before), nil
	})
	report["joiner_arrival"] = arrived
	report["joiner_arrival_ticks"] = elapsed
	if err != nil {
		return err
	}
	report["colonists_after"] = len(na.AsSlice(arrived["colonists"]))
	return nil
}
