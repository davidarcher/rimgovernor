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
		Name: "pawn/drug-policy",
		Scope: "An adult with no chemical trait, tolerance or addiction and an eight-year-old child each get the drug " +
			"policy labelled with their short name through a DrugPolicyIntent and PawnSettingsIntent.drug_policy " +
			"(#1537): the adult's allows beer for joy, the child's allows nothing. Native contract: the policy write, " +
			"the assignment and the policy facts' drug entries; the choice of entries itself is " +
			"policy/drug_policy_test.go.",
		Start:  cases.Fixture{On: cases.LabStart(), Op: "test/drinker_and_child"},
		Serve:  &cases.ServeSpec{Families: []routinefamily.Family{routinefamily.Work}},
		Budget: 4 * time.Minute,
		Run:    drugPolicy,
	})
}

func drugPolicy(ctx context.Context, s cases.Session) error {
	adult, child := na.AsString(s.Prepared()["adult"]), na.AsString(s.Prepared()["child"])
	if adult == "" || child == "" {
		return fmt.Errorf("fixture returned no pawns")
	}
	s.Report()["adult"], s.Report()["child"] = adult, child
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
				if v, ok := progress.Action().PawnSettings(); ok && v.Kind() == domain.SettingDrugPolicy && progress.View().Stage == domain.Completed {
					done[string(v.Pawn())] = true
				}
			}
		}
		if done[adult] && done[child] {
			return "drug policies assigned", true, nil
		}
		return "waiting for both drug policy assignments", false, nil
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
	joy, held := map[string][]string{}, map[string]bool{}
	for _, p := range facts.Drug {
		for _, pawn := range p.PawnIds {
			held[pawn] = true
			for _, e := range p.DrugEntries {
				if e.GetAllowedForJoy() {
					joy[pawn] = append(joy[pawn], e.GetDrugDef())
				}
				if e.GetAllowedForAddiction() || e.GetAllowScheduled() || e.GetTakeToInventory() != 0 {
					return fmt.Errorf("%s's drug policy %q allows more than joy: %v", pawn, p.GetLabel(), e)
				}
			}
			s.Report()["policy_"+pawn] = p.GetLabel()
		}
	}
	s.Report()["adult_joy"], s.Report()["child_joy"] = fmt.Sprint(joy[adult]), fmt.Sprint(joy[child])
	beer := false
	for _, d := range joy[adult] {
		beer = beer || d == "Beer"
	}
	if !held[adult] || !beer {
		return fmt.Errorf("adult's drug policy allows %v for joy, want beer", joy[adult])
	}
	if !held[child] || len(joy[child]) != 0 {
		return fmt.Errorf("child's drug policy allows %v for joy, want none", joy[child])
	}
	return nil
}
