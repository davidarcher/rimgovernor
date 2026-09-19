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
		Name: "takeover/draft", Scope: "Manual player draft is adopted under Auto through native CAS, then exact owned cleanup undrafts it; independent pawn readbacks prove both effects.",
		Start: cases.DebugStart{}, RequiredOps: []string{"test/b04f_setup"}, Budget: 2 * time.Minute, Run: runDraftTakeover,
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
	if drafted, _ := na.AsBool(player["drafted"]); !drafted || !na.DeepEqual(player["draftClaim"], map[string]any{"unowned": map[string]any{}}) {
		return fmt.Errorf("player draft is not standing and unowned: %v", player)
	}
	grant, err := na.GrantAuto(ctx, h.WireFunc(), "takeover-auto", identity)
	if err != nil {
		return err
	}
	request := na.ExecuteRequest(identity, grant, player, 1)
	reply, err := h.Wire(ctx, "auto-adopt", "operations_execute", request)
	if err != nil {
		return err
	}
	_, receipt, err := na.Outcome(reply, "receipt")
	if err != nil {
		return err
	}
	precondition, _ := na.AsMap(request["precondition"])
	if err = takeover.Receipt(ctx, h, map[string]any{"identity": identity, "attempt": precondition["attempt"]}, receipt, s.Report()); err != nil {
		return err
	}
	adopted, err := read("auto-adopt-readback", id)
	if err != nil {
		return err
	}
	if err = na.OwnedEffect(receipt, adopted, "applied", false); err != nil {
		return err
	}
	s.Report()["adopted"] = adopted
	cleanup, err := na.ReleaseRequest(identity, adopted)
	if err != nil {
		return err
	}
	released, err := h.Wire(ctx, "auto-release", "operations_release_owned_draft", cleanup)
	if err != nil {
		return err
	}
	if _, _, err = na.Outcome(released, "released"); err != nil {
		return err
	}
	after, err := read("auto-release-readback", id)
	if err != nil {
		return err
	}
	if drafted, _ := na.AsBool(after["drafted"]); drafted || !na.DeepEqual(after["draftClaim"], map[string]any{"unowned": map[string]any{}}) {
		return fmt.Errorf("auto failed to release adopted player draft: %v", after)
	}
	s.Report()["released"] = after
	return nil
}
