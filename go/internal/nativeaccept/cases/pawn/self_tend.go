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
	"google.golang.org/protobuf/encoding/protojson"
)

func init() {
	cases.Register(cases.Case{
		Name: "pawn/self-tend",
		Scope: "A lone doctor-capable colonist with self-tend off has no other doctor, so EnsureWorkAssignments turns " +
			"self-tend on through a PawnSettingsIntent (#1305), and the native care read shows it on. Native contract: " +
			"the intent's self_tend write, the care read's self_tend and medical_tend_quality; the comparison itself " +
			"is policy/self_tend_test.go.",
		Start:  cases.Fixture{On: cases.LabStart(), Op: "test/lone_self_tend_off"},
		Serve:  &cases.ServeSpec{Families: []string{"work"}},
		Budget: 4 * time.Minute,
		Run:    selfTend,
	})
}

func selfTend(ctx context.Context, s cases.Session) error {
	pawnID := na.AsString(s.Prepared()["pawn"])
	if pawnID == "" {
		return fmt.Errorf("fixture returned no pawn")
	}
	s.Report()["pawn"] = pawnID
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
				if v, ok := progress.Action().PawnSettings(); ok && string(v.Pawn()) == pawnID && v.Kind() == domain.SettingSelfTend && v.SelfTend() && progress.View().Stage == domain.Completed {
					return "self-tend applied", true, nil
				}
			}
		}
		return "waiting for the self-tend setting", false, nil
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
	reply, _, err := h.Client.ReadRoutinePawns(ctx, id, []string{pawnID})
	if err != nil {
		return err
	}
	rows := reply.GetObserved().GetPawns()
	if len(rows) != 1 || rows[0].GetPawn().GetId() != pawnID {
		return fmt.Errorf("fixture pawn missing from pawn read")
	}
	settings := rows[0].GetSettings()
	s.Report()["self_tend"] = settings.GetSelfTend()
	s.Report()["tend_quality"] = settings.GetPolicyInputs().GetMedicalTendQuality()
	if !settings.GetSelfTend() {
		return fmt.Errorf("self-tend readback off, want on")
	}
	if settings.GetPolicyInputs().MedicalTendQuality == nil {
		return fmt.Errorf("doctor-capable pawn read carried no medical tend quality")
	}
	return nil
}
