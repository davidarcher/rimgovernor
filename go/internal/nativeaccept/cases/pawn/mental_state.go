package pawn

import (
	"context"
	"encoding/json"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/encoding/protojson"
)

func init() {
	cases.Register(cases.Case{
		Name: "pawn/mental-state", Scope: "Induced Berserk is read as its native defName, aggression and zero initial age through pawn and colonist status reads, including the Go Fact projection.",
		Start: cases.Fixture{Op: "test/mental_state_berserk"}, Budget: time.Minute,
		Run: mentalState,
	})
}

func mentalState(ctx context.Context, s cases.Session) error {
	pawnID := na.AsString(s.Prepared()["pawn"])
	if pawnID == "" {
		return fmt.Errorf("fixture returned no pawn")
	}
	data, err := json.Marshal(s.Identity())
	if err != nil {
		return err
	}
	id := &c.Identity{}
	if err := protojson.Unmarshal(data, id); err != nil {
		return err
	}
	h := s.Harness()
	reply, _, err := h.Client.ReadPawns(ctx, id, []string{pawnID})
	if err != nil {
		return err
	}
	rows := reply.GetObserved().GetPawns()
	if len(rows) != 1 || rows[0].GetPawn().GetId() != pawnID {
		return fmt.Errorf("fixture pawn missing from pawn read")
	}
	fact, err := bridge.PawnMentalState(rows[0])
	if err != nil {
		return err
	}
	state, known := fact.Value()
	if !known || state.DefName != "Berserk" || !state.IsAggro || state.TicksInState != 0 {
		return fmt.Errorf("berserk readback: known=%v state=%+v", known, state)
	}
	s.Report()["pawn_mental_state"] = state
	status, _, err := h.Client.ReadEmergency(ctx, id)
	if err != nil {
		return err
	}
	for _, pawn := range status.Facts.Colonists {
		if string(pawn.ID) != pawnID {
			continue
		}
		observed, known := pawn.MentalState.Value()
		if !known || observed != state {
			return fmt.Errorf("colonist mental state disagrees: known=%v state=%+v", known, observed)
		}
		s.Report()["colonist_mental_state"] = observed
		return nil
	}
	return fmt.Errorf("berserk colonist missing from status")
}
