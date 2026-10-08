package pawn

import (
	"context"
	"encoding/json"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/routinefamily"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/encoding/protojson"
)

func init() {
	cases.Register(cases.Case{
		Name: "pawn/nickname",
		Scope: "The newest fixture colonist given the oldest one's nickname is renamed by EnsureWorkAssignments through a " +
			"PawnSettingsIntent nickname (#1310), and the population read's owned names show two distinct, unnumbered " +
			"short names. Native contract: the owned-name census and the name-bank draw; the collision rule is " +
			"policy/pawn_names_test.go.",
		Start:  cases.Fixture{On: cases.LabStart(), Op: "test/duplicate_nickname"},
		Serve:  &cases.ServeSpec{Families: []routinefamily.Family{routinefamily.Work}},
		Budget: 4 * time.Minute,
		Crew:   cases.Crew{Size: 3}, Run: nickname,
	})
}

func nickname(ctx context.Context, s cases.Session) error {
	pawnID, keeperID, name := na.AsString(s.Prepared()["pawn"]), na.AsString(s.Prepared()["keeper"]), na.AsString(s.Prepared()["name"])
	if pawnID == "" || keeperID == "" || name == "" {
		return fmt.Errorf("fixture returned no duplicate pair")
	}
	s.Report()["pawn"], s.Report()["name"] = pawnID, name
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
				if v, ok := progress.Action().PawnSettings(); ok && string(v.Pawn()) == pawnID && v.LeaveName() != "" && progress.View().Stage == domain.Completed {
					return "rename applied", true, nil
				}
			}
		}
		return "waiting for the rename", false, nil
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
	census, _, err := h.Client.ReadRoundsPopulation(ctx, id)
	if err != nil {
		return err
	}
	names, _ := census.Names.Value()
	short := map[string]string{}
	for _, n := range names {
		short[string(n.Pawn)] = n.Short
	}
	got := short[pawnID]
	s.Report()["renamed"] = got
	if short[keeperID] != name {
		return fmt.Errorf("keeper short name %q, want %q", short[keeperID], name)
	}
	if got == "" || strings.EqualFold(got, name) || strings.ContainsAny(got, "0123456789") {
		return fmt.Errorf("renamed short name %q, want a fresh unnumbered name other than %q", got, name)
	}
	return nil
}
