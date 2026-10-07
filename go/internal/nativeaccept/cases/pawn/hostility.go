package pawn

import (
	"context"
	"encoding/json"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/routinefamily"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/encoding/protojson"
)

func init() {
	cases.Register(cases.Case{
		Name: "pawn/hostility",
		Scope: "A fixture pawn made violence-incapable and set to Ignore is set to Flee by EnsureWorkAssignments through a " +
			"PawnSettingsIntent (#1299), and the native pawn read shows Flee. Native contract: the intent's write, " +
			"the care read's hostility_response and the violence tag; the mapping itself is policy/hostility_test.go.",
		Start:  cases.Fixture{On: cases.LabStart(), Op: "test/pacifist_ignore"},
		Serve:  &cases.ServeSpec{Families: []routinefamily.Family{routinefamily.Work}},
		Budget: 4 * time.Minute,
		Run:    hostility,
	})
}

func hostility(ctx context.Context, s cases.Session) error {
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
				if v, ok := progress.Action().PawnSettings(); ok && string(v.Pawn()) == pawnID && v.Hostility() == domain.HostilityFlee && progress.View().Stage == domain.Completed {
					return "Flee applied", true, nil
				}
			}
		}
		return "waiting for the Flee setting", false, nil
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
	reply, _, err := h.Client.ReadRoundsPawns(ctx, id, []string{pawnID})
	if err != nil {
		return err
	}
	rows := reply.GetObserved().GetPawns()
	if len(rows) != 1 || rows[0].GetPawn().GetId() != pawnID {
		return fmt.Errorf("fixture pawn missing from pawn read")
	}
	got := rows[0].GetSettings().GetHostilityResponse()
	s.Report()["hostility"] = got
	if got != op.HostilityResponse_HOSTILITY_RESPONSE_FLEE {
		return fmt.Errorf("hostility readback %v, want Flee", got)
	}
	return nil
}
