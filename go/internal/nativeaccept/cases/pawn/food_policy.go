package pawn

import (
	"context"
	"encoding/json"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
)

func init() {
	cases.Register(cases.Case{
		Name: "pawn/food-policy",
		Scope: "A cannibal and a vegetarian each get the food policy labelled with their short name through a " +
			"FoodPolicyIntent and PawnSettingsIntent.food_policy (#1541): the cannibal's allows human meat, the " +
			"vegetarian's no meat, and they differ. Native contract: the policy write, the assignment, the policy " +
			"facts' food kinds and allowed definitions, and the Ideology precept read; the diets themselves are " +
			"policy/food_policy_test.go.",
		Start:      cases.Fixture{On: cases.LabStart(), Op: "test/cannibal_and_vegetarian"},
		Expansions: []string{"ludeon.rimworld.ideology"},
		NoKeep:     true,
		Serve:      &cases.ServeSpec{Families: []string{"work"}},
		Budget:     4 * time.Minute,
		Run:        foodPolicy,
	})
}

func foodPolicy(ctx context.Context, s cases.Session) error {
	cannibal, vegetarian := na.AsString(s.Prepared()["cannibal"]), na.AsString(s.Prepared()["vegetarian"])
	if cannibal == "" || vegetarian == "" {
		return fmt.Errorf("fixture returned no pawns")
	}
	s.Report()["cannibal"], s.Report()["vegetarian"] = cannibal, vegetarian
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
	defer journal.Close()
	err = na.WaitProgress(ctx, na.Wait{Ceiling: 2 * time.Minute, Stall: 60 * time.Second, Interval: time.Second, Terminal: service.Exited}, func(ctx context.Context) (string, bool, error) {
		plans, err := journal.PlanHistoryWithMethods(ctx, 256, "work-*")
		if err != nil {
			return "", false, err
		}
		done := map[string]bool{}
		for _, p := range plans {
			for _, progress := range p.Progress {
				if v, ok := progress.Action().PawnSettings(); ok && v.Kind() == domain.SettingFoodPolicy && progress.View().Stage == domain.Completed {
					done[string(v.Pawn())] = true
				}
			}
		}
		if done[cannibal] && done[vegetarian] {
			return "food policies assigned", true, nil
		}
		return "waiting for both food policy assignments", false, nil
	})
	if err != nil {
		return err
	}
	service.Stop()
	h, err := s.Reattach(ctx)
	if err != nil {
		return err
	}
	data, err := json.Marshal(s.Identity())
	if err != nil {
		return err
	}
	id := &c.Identity{}
	if err := protojson.Unmarshal(data, id); err != nil {
		return err
	}
	reply, _, err := h.Client.ReadColonyFacts(ctx, id, false)
	if err != nil {
		return err
	}
	facts := reply.GetObserved().GetPolicies().GetObserved()
	if facts == nil {
		return fmt.Errorf("colony read carried no policy facts")
	}
	kinds := map[string]o.FoodKind{}
	for _, f := range facts.Foods {
		kinds[f.GetDefName()] = f.GetKind()
	}
	held := map[string]map[o.FoodKind]bool{}
	policy := map[string]string{}
	for _, p := range facts.Food {
		for _, pawn := range p.PawnIds {
			held[pawn] = map[o.FoodKind]bool{}
			for _, d := range p.AllowedDefs {
				held[pawn][kinds[d]] = true
			}
			policy[pawn] = p.GetId()
			s.Report()["policy_"+pawn] = p.GetLabel()
		}
	}
	s.Report()["cannibal_foods"], s.Report()["vegetarian_foods"] = fmt.Sprint(held[cannibal]), fmt.Sprint(held[vegetarian])
	if policy[cannibal] == "" || policy[cannibal] == policy[vegetarian] {
		return fmt.Errorf("cannibal and vegetarian share food policy %q", policy[cannibal])
	}
	if !held[cannibal][o.FoodKind_FOOD_KIND_HUMAN_MEAT] {
		return fmt.Errorf("cannibal's food policy allows %v, want human meat", held[cannibal])
	}
	for _, k := range []o.FoodKind{o.FoodKind_FOOD_KIND_HUMAN_MEAT, o.FoodKind_FOOD_KIND_RAW_MEAT, o.FoodKind_FOOD_KIND_INSECT_MEAT} {
		if held[vegetarian][k] {
			return fmt.Errorf("vegetarian's food policy allows %v, want no meat", held[vegetarian])
		}
	}
	if !held[vegetarian][o.FoodKind_FOOD_KIND_MEAL_SIMPLE] {
		return fmt.Errorf("vegetarian's food policy allows %v, want simple meals", held[vegetarian])
	}
	return nil
}
