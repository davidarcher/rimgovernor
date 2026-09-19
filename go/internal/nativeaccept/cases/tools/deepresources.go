package tools

import (
	"context"
	"encoding/json"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/sustained"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"time"
)

func init() {
	cases.Register(cases.Case{Name: "tools/deepresources", Scope: "Seeded deep resource lumps aggregate by connected definition, and both scanner types expose native state without drills or bills (#482).",
		Start: cases.Fixture{Op: "test/deep_resources_seed", On: cases.Save{Name: sustained.BaselineSave, From: cases.CommittedSaves()}}, Budget: 3 * time.Minute,
		Run: func(ctx context.Context, s cases.Session) error {
			h := s.Harness()
			data, err := json.Marshal(s.Prepared())
			if err != nil {
				return err
			}
			var expected struct {
				Success bool
				X, Z    int32
			}
			if err = json.Unmarshal(data, &expected); err != nil || !expected.Success {
				return fmt.Errorf("seed failed: %s (%v)", data, err)
			}
			identity, err := na.ReadIdentity(ctx, h, "deep-resources-identity")
			if err != nil {
				return err
			}
			raw, err := h.Wire(ctx, "deep-resources-readback", "observations_read_colony_facts", map[string]any{"scope": map[string]any{"expectedIdentity": identity}})
			if err != nil {
				return err
			}
			data, err = json.Marshal(raw)
			if err != nil {
				return err
			}
			reply := &o.ColonyFactsReply{}
			if err = protojson.Unmarshal(data, reply); err != nil {
				return err
			}
			v := reply.GetObserved()
			if v == nil {
				return fmt.Errorf("colony unavailable: %s", data)
			}
			if err = bridge.ValidateColonyFacts(v, v.Context.Identity); err != nil {
				return err
			}
			f := v.GetDeepResources().GetObserved()
			if f == nil {
				return fmt.Errorf("deep resources unavailable: %v", v.DeepResources)
			}
			found := 0
			for _, lump := range f.Lumps {
				x, z := lump.Centre.GetX(), lump.Centre.GetZ()
				if x < expected.X-5 || x > expected.X+5 || z < expected.Z-5 || z > expected.Z+5 {
					continue
				}
				valid := lump.GetDefName() == "Plasteel" && ((x == expected.X && z == expected.Z && lump.GetCount() == 300 && lump.GetCellCount() == 3) || (x == expected.X+4 && z == expected.Z && lump.GetCount() == 25 && lump.GetCellCount() == 1))
				valid = valid || lump.GetDefName() == "Steel" && x == expected.X+3 && z == expected.Z+3 && lump.GetCount() == 70 && lump.GetCellCount() == 2
				if !valid {
					return fmt.Errorf("incorrect lump aggregation: %v", lump)
				}
				found++
			}
			if found != 3 {
				return fmt.Errorf("got %d fixture lumps, want 3", found)
			}
			for _, rows := range [][]*o.MineralScannerState{f.GroundScanners, f.LongRangeScanners} {
				foundScanner := false
				for _, row := range rows {
					if row.Position.GetX() != expected.X-12 {
						continue
					}
					if !row.GetBuilt() || row.Powered == nil || row.GetPowered() || row.Working == nil || row.GetWorking() || row.TicksToNextFind == nil || row.GetTicksToNextFind() <= 0 {
						return fmt.Errorf("incorrect scanner state: %v", row)
					}
					foundScanner = true
				}
				if !foundScanner {
					return fmt.Errorf("fixture scanner missing: %v", rows)
				}
			}
			s.Report()["deep_resources"] = f
			return nil
		},
	})
}
