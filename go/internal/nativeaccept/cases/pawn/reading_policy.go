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
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/encoding/protojson"
)

func init() {
	cases.Register(cases.Case{
		Name: "pawn/reading-policy",
		Scope: "A researcher and an eight-year-old child each get the reading policy labelled with their short name " +
			"through a ReadingPolicyIntent and PawnSettingsIntent.reading_policy (#1306): the researcher's allows " +
			"schematics, the child's textbooks only, and neither allows a tome. Native contract: the policy write, " +
			"the assignment and the policy facts' book kinds and allowed definitions; the choice of books itself is " +
			"policy/reading_policy_test.go.",
		Start:  cases.Fixture{On: cases.LabStart(), Op: "test/reader_and_child"},
		Serve:  &cases.ServeSpec{Families: []routinefamily.Family{routinefamily.Work}},
		Budget: 4 * time.Minute,
		Run:    readingPolicy,
	})
}

func readingPolicy(ctx context.Context, s cases.Session) error {
	researcher, child := na.AsString(s.Prepared()["researcher"]), na.AsString(s.Prepared()["child"])
	if researcher == "" || child == "" {
		return fmt.Errorf("fixture returned no pawns")
	}
	s.Report()["researcher"], s.Report()["child"] = researcher, child
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
				if v, ok := progress.Action().PawnSettings(); ok && v.Kind() == domain.SettingReadingPolicy && progress.View().Stage == domain.Completed {
					done[string(v.Pawn())] = true
				}
			}
		}
		if done[researcher] && done[child] {
			return "reading policies assigned", true, nil
		}
		return "waiting for both reading policy assignments", false, nil
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
	kinds := map[string]policy.BookKind{}
	for _, b := range catalog.Books() {
		kinds[b.Def] = b.Kind
	}
	held := map[string][]policy.BookKind{}
	for _, p := range facts.Reading {
		for _, pawn := range p.PawnIds {
			for _, d := range p.AllowedDefs {
				held[pawn] = append(held[pawn], kinds[d])
			}
			s.Report()["policy_"+pawn] = p.GetLabel()
		}
	}
	has := func(rows []policy.BookKind, k policy.BookKind) bool {
		for _, r := range rows {
			if r == k {
				return true
			}
		}
		return false
	}
	s.Report()["researcher_books"], s.Report()["child_books"] = fmt.Sprint(held[researcher]), fmt.Sprint(held[child])
	if !has(held[researcher], policy.Schematic) || has(held[researcher], policy.Tome) {
		return fmt.Errorf("researcher's reading policy allows %v, want schematics and no tome", held[researcher])
	}
	if len(held[child]) == 0 {
		return fmt.Errorf("child's reading policy allows no book")
	}
	for _, k := range held[child] {
		if k != policy.Textbook {
			return fmt.Errorf("child's reading policy allows %v, want textbooks only", held[child])
		}
	}
	return nil
}
