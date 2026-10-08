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
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/encoding/protojson"
)

func init() {
	cases.Register(cases.Case{
		Name: "pawn/drug-addiction",
		Scope: "A colonist addicted to luciferium, with luciferium in stock, gets a drug policy that schedules " +
			"luciferium and allows it for the addiction (#1538). Native contract: the colony item census feeding the " +
			"planner, the chemical state read and the written scheduled entry; maintain-or-wean choices are " +
			"policy/drug_policy_test.go.",
		Start:  cases.Fixture{On: cases.LabStart(), Op: "test/luciferium_addict"},
		Serve:  &cases.ServeSpec{Families: []routinefamily.Family{routinefamily.Work}},
		Budget: 4 * time.Minute,
		Crew:   cases.Crew{Size: 3}, Run: drugAddiction,
	})
}

func drugAddiction(ctx context.Context, s cases.Session) error {
	pawn := na.AsString(s.Prepared()["pawn"])
	if pawn == "" {
		return fmt.Errorf("fixture returned no pawn")
	}
	s.Report()["pawn"] = pawn
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
				if v, ok := progress.Action().PawnSettings(); ok && v.Kind() == domain.SettingDrugPolicy && string(v.Pawn()) == pawn && progress.View().Stage == domain.Completed {
					return "drug policy assigned", true, nil
				}
			}
		}
		return "waiting for the drug policy assignment", false, nil
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
	for _, p := range facts.Drug {
		for _, holder := range p.PawnIds {
			if holder != pawn {
				continue
			}
			s.Report()["policy"] = p.GetLabel()
			for _, e := range p.DrugEntries {
				if e.GetDrugDef() == "Luciferium" {
					s.Report()["luciferium"] = fmt.Sprint(e)
					if !e.GetAllowScheduled() || !e.GetAllowedForAddiction() {
						return fmt.Errorf("luciferium entry %v does not schedule and allow for addiction", e)
					}
					return nil
				}
			}
			return fmt.Errorf("drug policy %q has no luciferium entry", p.GetLabel())
		}
	}
	return fmt.Errorf("%s holds no observed drug policy", pawn)
}
