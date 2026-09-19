package food

import (
	"context"
	"fmt"
	"strings"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{Name: "food/milk-eggs", Scope: "A ready cow and four hens supply the only food; ordinary milk/egg jobs increase native runway and the live food plan explains the derived cow population floor.",
		Start: cases.Fixture{On: EmptyChannels("MealSurvivalPack", 0), Op: "test/milk_eggs_prepare"}, RequiredOps: []string{observeOp}, Service: true, Budget: 4 * time.Minute, Run: milkEggs})
}

func milkEggs(ctx context.Context, s cases.Session) error {
	h := s.Harness()
	before, err := h.Call(ctx, "milk-eggs-before", observeOp, nil)
	if err != nil {
		return err
	}
	if len(na.AsSlice(before["stock"])) != 0 || na.AsNumber(before["fields"]) != 0 || na.AsNumber(before["animals"]) != 5 {
		return fmt.Errorf("fixture has competing food channels: %v", before)
	}
	s.Report()["before"] = before
	if _, err = na.ConfirmColonyNames(ctx, h, s.Report()); err != nil {
		return err
	}
	service, err := s.Launch(ctx, na.ServiceLaunch{Families: []string{"husbandry"}, Extra: na.ClockSpeedArgs()})
	if err != nil {
		return err
	}
	defer service.Stop()
	token, err := service.SessionToken()
	if err != nil {
		return err
	}
	if _, err = service.WaitAttached(s.Identity(), 90*time.Second); err != nil {
		return err
	}
	if _, err = service.Resume("milk-eggs", s.Identity(), token, s.Report()); err != nil {
		return err
	}
	keep := (&na.AuthorityKeepAlive{Service: service, Prefix: "milk-eggs", Identity: s.Identity(), Token: token}).Start(ctx)
	defer keep()
	err = na.WaitProgress(ctx, na.Wait{Ceiling: 2 * time.Minute, Stall: na.StallBudget(), Terminal: service.Exited}, func(ctx context.Context) (string, bool, error) {
		colony, status, e := service.API("GET", "/api/player/colony", nil, "")
		if e != nil {
			return "", false, e
		}
		if status != 200 {
			return "", false, fmt.Errorf("colony status %d", status)
		}
		plan, _ := na.AsMap(colony["foodPlan"])
		explain := na.AsString(plan["explain"])
		s.Report()["colony"] = colony
		ready := na.AsNumber(colony["foodRunwayDays"]) > 0 && na.AsNumber(colony["foodNutrition"]) > 1.0 && strings.Contains(explain, "MaintainHerd-Cow derived floor 1") && strings.Contains(explain, "AnimalProduct/Chicken")
		return na.Signature(colony["tick"], colony["foodNutrition"], explain), ready, nil
	})
	if err != nil {
		return err
	}
	keep()
	service.Stop()
	h, err = s.Reattach(ctx)
	if err != nil {
		return err
	}
	after, err := h.Call(ctx, "milk-eggs-after", observeOp, nil)
	if err != nil {
		return err
	}
	s.Report()["after"] = after
	milk, eggs := 0.0, 0.0
	for _, item := range na.AsSlice(after["stock"]) {
		row, _ := na.AsMap(item)
		def := na.AsString(row["defName"])
		switch {
		case def == "Milk":
			milk += na.AsNumber(row["units"])
		case strings.HasPrefix(def, "EggChicken"):
			eggs += na.AsNumber(row["units"])
		default:
			return fmt.Errorf("competing food appeared: %v", row)
		}
	}
	if milk <= 0 || eggs <= 0 || na.AsNumber(after["fields"]) != 0 {
		return fmt.Errorf("milk and eggs not both gathered without fields: %v", after)
	}
	return nil
}
