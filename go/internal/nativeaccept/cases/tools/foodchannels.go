package tools

import (
	"context"
	"encoding/json"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/sustained"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
)

func init() {
	cases.Register(cases.Case{
		Name: "tools/foodchannels", Scope: "Core-only food source census: baseline forage and absent Odyssey water; a fixture cow reports native milk fullness (#421).",
		Start: cases.Save{Name: sustained.BaselineSave, From: cases.CommittedSaves()}, Budget: 3 * time.Minute,
		Run: func(ctx context.Context, s cases.Session) error {
			h := s.Harness()
			read := func(label string) (*o.FoodChannelsFacts, error) {
				identity, err := na.ReadIdentity(ctx, h, label+"-identity")
				if err != nil {
					return nil, err
				}
				raw, err := h.Wire(ctx, label, "observations_read_colony_facts", map[string]any{"scope": map[string]any{"expectedIdentity": identity}})
				if err != nil {
					return nil, err
				}
				data, err := json.Marshal(raw)
				if err != nil {
					return nil, err
				}
				reply := &o.ColonyFactsReply{}
				if err = protojson.Unmarshal(data, reply); err != nil {
					return nil, err
				}
				v := reply.GetObserved()
				if v == nil {
					return nil, fmt.Errorf("colony unavailable: %s", data)
				}
				if err = bridge.ValidateColonyFacts(v, v.Context.Identity); err != nil {
					return nil, err
				}
				f := v.GetFoodChannels().GetObserved()
				if f == nil {
					return nil, fmt.Errorf("food channels unavailable: %v", v.FoodChannels)
				}
				return f, nil
			}
			baseline, err := read("foodchannels-baseline")
			if err != nil {
				return err
			}
			if baseline.FishableWater != nil || len(baseline.Forage) == 0 {
				return fmt.Errorf("core baseline must have forage and no Odyssey fishing: %v", baseline)
			}
			s.Report()["baseline_forage"] = len(baseline.Forage)
			if _, err = h.Call(ctx, "foodchannels-cow-fixture", "test/husbandry_setup", map[string]any{}); err != nil {
				return err
			}
			herd, err := read("foodchannels-herd")
			if err != nil {
				return err
			}
			for _, row := range herd.Gatherable {
				if row.GetRace() == "Cow" && row.GetResource() == "Milk" && row.Fullness != nil && row.GetFullness() >= 0 && row.GetFullness() <= 1 {
					s.Report()["cow_fullness"] = row.GetFullness()
					return nil
				}
			}
			return fmt.Errorf("fixture cow lacks observed milk fullness: %v", herd.Gatherable)
		},
	})
}
