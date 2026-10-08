package pawn

import (
	"context"
	"encoding/json"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/routinefamily"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	pol "github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/encoding/protojson"
)

func init() {
	cases.Register(cases.Case{
		Name: "pawn/animal-food-policy",
		Scope: "A named tame dog gets the food policy labelled with its name through FoodPolicyIntent and " +
			"PawnSettingsIntent.food_policy (#1543): it allows kibble and never a meal. Native contract: " +
			"the FoodEater read (race edibility), the kibble kind, the assignment to an animal's food tracker; " +
			"the per-kind diets are policy/food_policy_test.go.",
		Start:  cases.Fixture{On: cases.LabStart(), Op: "test/tame_dog"},
		NoKeep: true,
		Serve:  &cases.ServeSpec{Families: []routinefamily.Family{routinefamily.Work}},
		Budget: 4 * time.Minute,
		Crew:   cases.Crew{Size: 3}, Run: animalFoodPolicy,
	})
}

func animalFoodPolicy(ctx context.Context, s cases.Session) error {
	dog := na.AsString(s.Prepared()["animal"])
	if dog == "" {
		return fmt.Errorf("fixture returned no animal")
	}
	s.Report()["animal"] = dog
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
		for _, p := range plans {
			for _, progress := range p.Progress {
				if v, ok := progress.Action().PawnSettings(); ok && v.Kind() == domain.SettingFoodPolicy && string(v.Pawn()) == dog && progress.View().Stage == domain.Completed {
					return "animal food policy assigned", true, nil
				}
			}
		}
		return "waiting for the animal's food policy assignment", false, nil
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
	catalog, err := h.Client.DefinitionCatalog(ctx, id)
	if err != nil {
		return err
	}
	kinds := map[string]pol.FoodKind{}
	for _, f := range catalog.Foods() {
		kinds[f.Def] = f.Kind
	}
	held := map[pol.FoodKind]bool{}
	for _, p := range facts.Food {
		for _, pawn := range p.PawnIds {
			if pawn != dog {
				continue
			}
			s.Report()["policy"] = p.GetLabel()
			for _, d := range p.AllowedDefs {
				held[kinds[d]] = true
			}
		}
	}
	s.Report()["foods"] = fmt.Sprint(held)
	if !held[pol.FoodKindKibble] {
		return fmt.Errorf("animal's food policy allows %v, want kibble", held)
	}
	for _, k := range []pol.FoodKind{pol.FoodKindMealAwful, pol.FoodKindMealSimple, pol.FoodKindMealFine, pol.FoodKindMealLavish} {
		if held[k] {
			return fmt.Errorf("animal's food policy allows %v, want no meal", held)
		}
	}
	return nil
}
