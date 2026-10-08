package lifecycle

import (
	"context"
	"encoding/json"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
	"slices"
	"time"
)

func init() {
	cases.Register(cases.Case{Name: "lifecycle/ideoligion-design", Scope: "#1662/#1663: a fresh main-menu catalog produces a deterministic fluid design; production colony creation applies the requested memes and plain precepts exactly and the live colony reports them. A snapshot cannot prove pre-map catalog availability or vanilla creation initialization.", Start: cases.Owned{}, Reason: "the production creation contract starts at a fresh main menu", Expansions: []string{"ludeon.rimworld.ideology"}, NoKeep: true, Quiet: na.QuietRequired, RequiredOps: []string{"test/ideoligion_design_inspect"}, Budget: 10 * time.Minute, Crew: cases.Crew{Size: 3}, Run: runIdeoligionCreation})
}
func runIdeoligionCreation(ctx context.Context, s cases.Session) error {
	return newColonyMenu(ctx, s.Config(), s.Report(), "ideoligion", func(h *na.Harness, row map[string]any) error {
		raw, err := h.Wire(ctx, "creation-catalog", "observations_read_definition_catalog", map[string]any{"creation": true})
		if err != nil {
			return err
		}
		encoded, err := json.Marshal(raw)
		if err != nil {
			return err
		}
		var reply o.DefinitionCatalogReply
		if err := protojson.Unmarshal(encoded, &reply); err != nil {
			return err
		}
		catalog, err := bridge.DecodeCreationCatalog(reply.GetCreation())
		if err != nil {
			return err
		}
		choice, err := catalog.StartingIdeoligion("Crashlanded")
		if err != nil {
			return err
		}
		second, err := catalog.StartingIdeoligion("Crashlanded")
		if err != nil {
			return err
		}
		want := bridge.WireIdeoligionDesign(choice.Design)
		if !proto.Equal(want, bridge.WireIdeoligionDesign(second.Design)) {
			return fmt.Errorf("selection is nondeterministic")
		}
		spec := newColonySpec()
		spec["storyteller"] = "Phoebe"
		spec["difficulty"] = "Peaceful"
		spec["saveName"] = "ideoligion-design-accept"
		encoded, err = protojson.Marshal(want)
		if err != nil {
			return err
		}
		spec["ideoligion"] = json.RawMessage(encoded)
		if _, _, err := newColonyRun(ctx, h, "ideoligion-create", spec, "ideoligion"); err != nil {
			return err
		}
		got, err := h.Call(ctx, "created-design", "test/ideoligion_design_inspect", map[string]any{})
		if err != nil {
			return err
		}
		var actual c.IdeoligionDesign
		if err := protojson.Unmarshal([]byte(na.AsString(got["design"])), &actual); err != nil {
			return err
		}
		slices.Sort(actual.Memes)
		slices.Sort(actual.Precepts)
		if !proto.Equal(&actual, want) || na.AsNumber(got["count"]) != 0 || na.AsNumber(got["points"]) != 0 {
			return fmt.Errorf("created design differs: %v", got)
		}
		row["design"] = got
		return nil
	})
}
