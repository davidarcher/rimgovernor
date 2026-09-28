package draft

import (
	"context"
	"fmt"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/takeover"
)

func init() {
	cases.Register(cases.Case{
		Name: "takeover/draft", Scope: "A Manual player draft is undrafted by the Auto undraft intent; pawn readbacks prove both effects.",
		Start: cases.LabStart(), RequiredOps: []string{"test/b04f_setup"}, Budget: 2 * time.Minute, Run: runDraftTakeover,
	})
}

func runDraftTakeover(ctx context.Context, s cases.Session) error {
	if err := takeover.Manual(ctx, s); err != nil {
		return err
	}
	h, identity := s.Harness(), s.Identity()
	read := func(label, id string) (map[string]any, error) {
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
	before, err := read("manual-pawn", "")
	if err != nil {
		return err
	}
	pawn, _ := na.AsMap(before["pawn"])
	id := na.AsString(pawn["id"])
	edit, err := h.Call(ctx, "manual-draft", "test/b04f_setup", map[string]any{"op": "external-draft", "pawn": id, "drafted": true})
	if err != nil {
		return err
	}
	if ok, _ := na.AsBool(edit["success"]); !ok {
		return fmt.Errorf("player draft refused: %v", edit)
	}
	player, err := read("manual-draft-readback", id)
	if err != nil {
		return err
	}
	if drafted, _ := na.AsBool(player["drafted"]); !drafted {
		return fmt.Errorf("player draft is not standing: %v", player)
	}
	if _, err = na.GrantAuto(ctx, h.WireFunc(), "takeover-auto", identity); err != nil {
		return err
	}
	// Auto has full control: no plan needs the player's draft, so the
	// undraft intent releases it (#939).
	if err := expectDraft(ctx, h, identity, "auto-undraft", id, false, true); err != nil {
		return err
	}
	s.Report()["undrafted"] = id
	return nil
}
