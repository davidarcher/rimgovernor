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
		Name: "pawn/medicine-carry",
		Scope: "A fixture doctor on NormalOrWorse care carrying no medicine, beside ample stock, is given a positive Medicine " +
			"carry count by EnsureWorkAssignments through a PawnSettingsIntent (#1307), and the native policy-inputs read " +
			"shows it. Native contract: the medicine_carry write and the inventory-stock read; the counts are policy/medicine_carry_test.go.",
		Start:  cases.Fixture{On: cases.LabStart(), Op: "test/doctor_medicine"},
		Serve:  &cases.ServeSpec{Families: []string{"work"}},
		Budget: 4 * time.Minute,
		Run:    medicineCarry,
	})
}

func medicineCarry(ctx context.Context, s cases.Session) error {
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
				v, ok := progress.Action().PawnSettings()
				if count, carry := v.MedicineCarry(); ok && carry && count > 0 && string(v.Pawn()) == pawnID && progress.View().Stage == domain.Completed {
					return "carry applied", true, nil
				}
			}
		}
		return "waiting for the medicine carry setting", false, nil
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
	count := int32(0)
	for _, stock := range rows[0].GetSettings().GetPolicyInputs().GetInventoryStock() {
		if stock.GetGroup() == "Medicine" {
			count = stock.GetCount()
		}
	}
	s.Report()["medicine_carry"] = count
	if count < 1 {
		return fmt.Errorf("medicine carry readback %d, want at least 1", count)
	}
	return nil
}
