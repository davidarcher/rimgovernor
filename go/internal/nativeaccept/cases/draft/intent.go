package draft

import (
	"context"
	"fmt"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{
		Name: "draft/intent",
		Scope: "The draft intent through Actions/Apply (#939): draft issues, a same-state draft applies with issued=false, " +
			"an unknown pawn is refused, and undraft issues; census readbacks prove each effect.",
		Start: cases.LabStart(), Budget: 2 * time.Minute, Run: runIntent,
	})
}

// readPawn returns the census row for id, or the first colonist when id is empty.
func readPawn(ctx context.Context, h *na.Harness, identity map[string]any, label, id string) (map[string]any, error) {
	filter := map[string]any{"colonist": true, "downed": false}
	if id != "" {
		filter["ids"] = []string{id}
	}
	reply, err := h.Wire(ctx, label, "observations_list_pawns", map[string]any{"scope": map[string]any{"expectedIdentity": identity}, "filter": filter})
	if err != nil {
		return nil, err
	}
	return na.PawnRow(reply, identity, id)
}

// expectDraft applies one draft intent and checks issued and the census readback.
func expectDraft(ctx context.Context, h *na.Harness, identity map[string]any, key, id string, drafted, issued bool) error {
	job, err := na.ApplyDraft(ctx, h, key, identity, key, id, drafted)
	if err != nil {
		return err
	}
	if got, _ := na.AsBool(job["issued"]); got != issued {
		return fmt.Errorf("%s: issued=%v, want %v: %#v", key, got, issued, job)
	}
	row, err := readPawn(ctx, h, identity, key+"-readback", id)
	if err != nil {
		return err
	}
	if got, _ := na.AsBool(row["drafted"]); got != drafted {
		return fmt.Errorf("%s: census drafted=%v, want %v", key, got, drafted)
	}
	return nil
}

func runIntent(ctx context.Context, s cases.Session) error {
	h, identity := s.Harness(), s.Identity()
	before, err := readPawn(ctx, h, identity, "initial-pawn", "")
	if err != nil {
		return err
	}
	if drafted, _ := na.AsBool(before["drafted"]); drafted {
		return fmt.Errorf("fixture pawn starts drafted")
	}
	pawn, _ := na.AsMap(before["pawn"])
	id := na.AsString(pawn["id"])
	if _, err := na.GrantAuto(ctx, h.WireFunc(), "acquire", identity); err != nil {
		return err
	}
	if err := expectDraft(ctx, h, identity, "draft", id, true, true); err != nil {
		return err
	}
	if err := expectDraft(ctx, h, identity, "draft-again", id, true, false); err != nil {
		return err
	}
	if _, err := na.ApplyDraft(ctx, h, "draft-unknown", identity, "draft-unknown", "NoSuchPawn0", true); err == nil {
		return fmt.Errorf("draft-unknown: an unknown pawn was not refused")
	}
	if err := expectDraft(ctx, h, identity, "undraft", id, false, true); err != nil {
		return err
	}
	s.Report()["pawn_id"] = id
	return cases.CheckStartupLog(s)
}
