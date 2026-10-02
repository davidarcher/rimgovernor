package production

import (
	"context"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{
		Name: "production/per-pawn-outfits",
		Scope: "MaintainEquipment puts every lab colonist on its own outfit, labelled with its short name, then prunes every " +
			"other outfit, the vanilla ones included (#1302). End-to-end signal over the ApparelPolicyIntent and " +
			"PolicyPruneIntent: the filters themselves are policy/apparel_policy_test.go.",
		Start:  cases.LabStart(),
		Serve:  &cases.ServeSpec{Families: []string{"gear"}},
		Budget: 4 * time.Minute,
		Run:    perPawnOutfits,
	})
}

func perPawnOutfits(ctx context.Context, s cases.Session) error {
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
	err = na.WaitProgress(ctx, na.Wait{Ceiling: 3 * time.Minute, Stall: 90 * time.Second, Interval: time.Second, Terminal: service.Exited}, func(ctx context.Context) (string, bool, error) {
		plans, err := journal.PlanHistoryWithMethods(ctx, 256, "outfit-prune-*")
		if err != nil {
			return "", false, err
		}
		for _, p := range plans {
			for _, progress := range p.Progress {
				if v, ok := progress.Action().PolicyPrune(); ok && v.Database() == domain.OutfitPolicies && progress.View().Stage == domain.Completed {
					return "outfits pruned", true, nil
				}
			}
		}
		return "waiting for per-pawn outfits and the outfit prune", false, nil
	})
	if err != nil {
		return err
	}
	service.Stop()
	h, err := s.Reattach(ctx)
	if err != nil {
		return err
	}
	read, err := h.Call(ctx, "outfits", "test/gear_fixture", map[string]any{"mode": "outfits"})
	if err != nil {
		return err
	}
	s.Report()["outfits"] = read
	held := map[string]bool{}
	for _, row := range na.AsSlice(read["pawns"]) {
		pawn, _ := na.AsMap(row)
		outfit := na.AsString(pawn["outfit"])
		if outfit == "" || na.AsString(pawn["label"]) != na.AsString(pawn["shortName"]) {
			return fmt.Errorf("pawn %v is not on its own outfit", pawn)
		}
		if held[outfit] {
			return fmt.Errorf("outfit %s is shared", outfit)
		}
		held[outfit] = true
	}
	if len(held) == 0 {
		return fmt.Errorf("no colonists read")
	}
	for _, id := range na.AsSlice(read["outfits"]) {
		if !held[na.AsString(id)] {
			return fmt.Errorf("outfit %v survived the prune", id)
		}
	}
	return nil
}
