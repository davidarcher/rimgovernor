package quest

import (
	"context"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func init() {
	cases.Register(cases.Case{
		Name:   "quest/wanderer",
		Scope:  "A native WandererJoin incident leaves its AcceptJoiner letter unanswered without population capacity; MaintainPopulation answers it under authority once capacity is declared, and the exact offered pawn arrives as a free colonist.",
		Start:  cases.Fixture{Op: joinerPrepareTool, Args: map[string]any{"skipQuest": true}, On: cases.Save{Name: joinerBaseline}},
		Serve:  &cases.ServeSpec{Families: []string{"population-joiner", "supply", "shelter"}, NativeTimeout: 15 * time.Second, Prefix: "quest-wanderer"},
		Budget: 10 * time.Minute,
		Run:    runWanderer,
	})
	cases.Register(cases.Case{
		Name:   "quest/wanderer-defense",
		Scope:  "An enabled raid threshold leaves the stocked colony's wanderer letter unanswered without a built defense tier; after native firing cover is built and recorded, the exact offered pawn joins.",
		Start:  cases.Fixture{Op: joinerPrepareTool, Args: map[string]any{"skipQuest": true}, On: cases.Save{Name: joinerBaseline}},
		Serve:  &cases.ServeSpec{Families: []string{"population-joiner", "supply", "shelter"}, NativeTimeout: 15 * time.Second, Prefix: "quest-wanderer-defense"},
		Budget: 10 * time.Minute,
		Run:    func(ctx context.Context, s cases.Session) error { return runWandererCapacity(ctx, s, true) },
	})
}

func runWanderer(ctx context.Context, s cases.Session) error {
	return runWandererCapacity(ctx, s, false)
}

func runWandererCapacity(ctx context.Context, s cases.Session, defense bool) error {
	h, report, identity := s.Harness(), s.Report(), s.Identity()
	if _, err := na.ConfirmColonyNames(ctx, h, report); err != nil {
		return err
	}
	var defenseSite domain.Cell
	if defense {
		var err error
		defenseSite, err = prepareWandererDefense(ctx, s)
		if err != nil {
			return err
		}
	}
	incident, err := h.Call(ctx, "wanderer-offer", "test/join_incident", map[string]any{"dryRun": false, "deferAnswer": true})
	if err != nil {
		return err
	}
	report["incident"] = incident
	if applied, _ := na.AsBool(incident["applied"]); !applied {
		return fmt.Errorf("wanderer incident not applied: %#v", incident)
	}
	if len(na.AsSlice(incident["joined"])) != 0 {
		return fmt.Errorf("fixture answered the letter")
	}
	census, err := h.Wire(ctx, "wanderer-census", "observations_read_colony_facts", map[string]any{"scope": map[string]any{"expectedIdentity": identity}, "page": map[string]any{"limit": 256}})
	if err != nil {
		return err
	}
	_, observed, err := na.Outcome(census, "observed")
	if err != nil {
		return err
	}
	letters := na.AsSlice(observed["joinerLetters"])
	if len(letters) != 1 {
		return fmt.Errorf("expected one pending joiner letter: %#v", letters)
	}
	letter, _ := na.AsMap(letters[0])
	report["letter"] = letter
	pawn := na.AsString(letter["pawnId"])
	token := na.AsString(letter["snapshotToken"])
	id := int32(na.AsNumber(letter["letterId"]))
	if pawn == "" || token == "" {
		return fmt.Errorf("letter omitted pawn or CAS token")
	}
	// A stale target must be refused before authority or admission is involved.
	stale, err := h.Wire(ctx, "stale-letter-preview", "operations_preview", map[string]any{"identity": identity, "operation": map[string]any{"answerDialog": map[string]any{"windowId": id, "optionIndex": 0, "optionLabel": na.AsString(letter["acceptLabel"]), "joinerLetterToken": token + "-stale"}}})
	if err != nil {
		return err
	}
	if _, ok := stale["failure"]; !ok {
		return fmt.Errorf("stale letter token was not refused: %#v", stale)
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
	answers := func() (bool, bool, string, error) {
		review, err := st.LoadRoutineReview(ctx)
		if err != nil {
			return false, false, "", err
		}
		found, complete := false, false
		signature := "waiting for letter plan"
		for _, binding := range review.Goals {
			if binding.Need != policy.MaintainPopulation {
				continue
			}
			goal, err := st.LoadGoal(ctx, binding.Goal)
			if err != nil {
				return false, false, "", err
			}
			for epoch := uint64(0); epoch <= goal.Goal.Epoch; epoch++ {
				methods, err := st.LoadGoalMethods(ctx, binding.Goal, epoch)
				if err != nil {
					return false, false, "", err
				}
				for _, method := range methods {
					plan, err := st.LoadPlan(ctx, method.Plan)
					if err != nil {
						return false, false, "", err
					}
					for i, action := range plan.Spec.Actions() {
						answer, ok := action.DialogAnswer()
						if !ok || answer.LetterToken() == "" {
							continue
						}
						if answer.WindowID() != id || answer.LetterToken() != token {
							return false, false, "", fmt.Errorf("wrong letter answered")
						}
						found = true
						stage := plan.Progress[i].View().Stage
						signature += string(stage)
						if stage == domain.Unsuccessful || stage == domain.Cancelled {
							return false, false, "", fmt.Errorf("letter answer ended %s", stage)
						}
						complete = complete || stage == domain.Completed
					}
				}
			}
		}
		return found, complete, signature, nil
	}
	if _, _, err := service.WaitRoutineReview(ctx, st, joinerCeiling); err != nil {
		return err
	}
	if found, _, _, err := answers(); err != nil || found {
		return fmt.Errorf("letter answered without a population policy: found=%v err=%v", found, err)
	}
	capacity := map[string]any{"maximum": joinerPolicyMaximum, "foodDays": joinerPolicyFoodDays}
	if defense {
		capacity["raidThreshold"] = 1.0
	}
	response, status, err := service.API("POST", "/api/player/population-policy/replace", map[string]any{"requestId": "wanderer-capacity", "expected": identity, "policy": capacity}, service.Token)
	if err != nil {
		return err
	}
	if status != 201 {
		return fmt.Errorf("population policy: %d %#v", status, response)
	}
	if defense {
		if err := proveWandererDefense(ctx, s, service, st, defenseSite, answers); err != nil {
			return err
		}
	}
	if err := na.WaitProgress(ctx, na.Wait{Ceiling: joinerCeiling, Stall: na.StallBudget(), Terminal: service.Exited}, func(context.Context) (string, bool, error) {
		_, complete, signature, err := answers()
		return signature, complete, err
	}); err != nil {
		return err
	}
	report["service_stop"] = service.Stop()
	h, err = s.Reattach(ctx)
	if err != nil {
		return err
	}
	after, err := h.Call(ctx, "wanderer-arrived", joinerReadTool, map[string]any{})
	if err != nil {
		return err
	}
	report["native_after"] = after
	for _, raw := range na.AsSlice(after["colonists"]) {
		if na.AsString(raw) == pawn {
			return nil
		}
	}
	return fmt.Errorf("the exact offered pawn %s did not arrive as a native free colonist", pawn)
}
