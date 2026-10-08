package pawn

import (
	"context"
	"encoding/json"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/encoding/protojson"
)

func init() {
	cases.Register(cases.Case{
		Name: "pawn/inspiration", Scope: "A fixture pawn given Inspired_Creativity reads it back through the pawn read and the Go pawn profile (#1187).",
		Start: cases.Fixture{On: cases.LabStart(), Op: "test/inspire_creativity"}, Budget: time.Minute, Crew: cases.Crew{Size: 3},
		Run: inspiration,
	})
}

func inspiration(ctx context.Context, s cases.Session) error {
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
	reply, _, err := s.Harness().Client.ReadPawns(ctx, id, []string{pawnID})
	if err != nil {
		return err
	}
	rows := reply.GetObserved().GetPawns()
	if len(rows) != 1 || rows[0].GetPawn().GetId() != pawnID {
		return fmt.Errorf("fixture pawn missing from pawn read")
	}
	catalog, err := s.Harness().Client.DefinitionCatalog(ctx, id)
	if err != nil {
		return err
	}
	work, err := observation.WorkPawnRow(rows[0], catalog, bridge.Things{})
	if err != nil {
		return err
	}
	got, known := policy.BuildProfile(work).Inspiration.Value()
	if !known || got != "Inspired_Creativity" {
		return fmt.Errorf("inspiration readback: known=%v got=%q", known, got)
	}
	s.Report()["pawn_inspiration"] = got
	return nil
}
