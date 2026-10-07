package farm

import (
	"context"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/routinefamily"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/sustainedfood"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

const nonFoodOp = "test/nonfood_field"

// nonFoodField is one end-to-end non-food field case (#2288): the lab colony
// has no stock of the product, no wild plant harvesting it, and a standing
// MaintainResource demand for it, so the only way to serve the demand is the
// generic field path (#2221): the supply plan prices a field, the field step
// places the growing zone, the colonists sow it and, once the fixture has
// matured the crop (its grow days are not what is under test), harvest it.
type nonFoodField struct {
	name, scope string
	plant       string
	product     string
	// concern is the standard whose deficit the field serves.
	concern policy.ConcernID
}

func init() {
	for _, c := range []nonFoodField{
		{
			name: "farm/cotton-field", plant: "Plant_Cotton", product: "Cloth", concern: policy.MaintainResource,
			scope: "Clothing floor with no cloth, leather or stored outfit and no wild cotton on the map: the generic field path opens a Plant_Cotton " +
				"growing zone, the colonists sow it, and the matured crop is harvested into cloth (#2288).",
		},
		{
			name: "farm/healroot-field", plant: "Plant_Healroot", product: "MedicineHerbal", concern: policy.MaintainMedicalReserves,
			scope: "Medicine reserve with no medicine and no wild healroot on the map: the generic field path opens a Plant_Healroot " +
				"growing zone, the colonists sow it, and the matured crop is harvested into herbal medicine (#2288).",
		},
	} {
		cases.Register(c.register())
	}
}

func (c nonFoodField) register() cases.Case {
	return cases.Case{
		Name:        c.name,
		Scope:       c.scope,
		Start:       cases.Fixture{Op: nonFoodOp, Args: map[string]any{"action": "prepare", "plant": c.plant, "product": c.product}, On: cases.LabStart()},
		RequiredOps: []string{nonFoodOp},
		Serve: &cases.ServeSpec{
			Families:      []routinefamily.Family{routinefamily.Field, routinefamily.Resource, routinefamily.Supply, routinefamily.Acquisition, routinefamily.Bill, routinefamily.Work, routinefamily.Medical, routinefamily.Tend, routinefamily.Rescue},
			NativeTimeout: 15 * time.Second, Prefix: "nonfood-field",
		},
		Budget: 40 * time.Minute,
		Reason: "sowing and harvest each wait for the planner to open the field and the colonists to work it: two serve phases on one colony",
		Run:    c.run,
	}
}

type fieldCensus struct {
	zones []map[string]any
	units int64
	wild  int64
}

func (c nonFoodField) census(ctx context.Context, h *na.Harness, label string) (fieldCensus, map[string]any, error) {
	reply, err := h.Call(ctx, label, nonFoodOp, map[string]any{"action": "census", "plant": c.plant, "product": c.product})
	if err != nil {
		return fieldCensus{}, nil, err
	}
	out := fieldCensus{units: int64(na.AsNumber(reply["productUnits"])), wild: int64(na.AsNumber(reply["wildPlants"]))}
	for _, raw := range na.AsSlice(reply["zones"]) {
		zone, _ := na.AsMap(raw)
		if na.AsString(zone["plant"]) == c.plant {
			out.zones = append(out.zones, zone)
		}
	}
	return out, reply, nil
}

func (f fieldCensus) sown() int64 {
	var n int64
	for _, z := range f.zones {
		n += int64(na.AsNumber(z["sown"]))
	}
	return n
}

// window is one serve phase's length in game ticks; a phase repeats up to
// phaseWindows times until its audit is satisfied.
const (
	nonFoodWindow = 40000
	phaseWindows  = 3
)

func (c nonFoodField) run(ctx context.Context, s cases.Session) error {
	report, prepared := s.Report(), s.Prepared()
	report["fixture"] = prepared
	if ok, _ := na.AsBool(prepared["success"]); !ok {
		return fmt.Errorf("fixture refused: %v", prepared)
	}

	// Phase 1: the planner opens the field and the colonists sow it.
	var sown fieldCensus
	for attempt := 1; attempt <= phaseWindows && sown.sown() == 0; attempt++ {
		_, err := sustainedfood.Observe(ctx, s, sustainedfood.Observation{
			WatchConfig: sustainedfood.WatchConfig{Watch: 12 * time.Minute, Window: nonFoodWindow, Concern: c.concern},
			Audit: func(ctx context.Context, h *na.Harness, report na.Report) error {
				var reply map[string]any
				var err error
				if sown, reply, err = c.census(ctx, h, fmt.Sprintf("sow-census-%d", attempt)); err != nil {
					return err
				}
				report["sow_census"] = reply
				if sown.sown() == 0 {
					return nil
				}
				// Phase 2 setup: grow the crop out rather than wait its grow days.
				grown, err := h.Call(ctx, "mature", nonFoodOp, map[string]any{"action": "mature", "plant": c.plant, "product": c.product})
				report["matured"] = grown
				return err
			},
		})
		if err != nil {
			return fmt.Errorf("sow window %d: %w", attempt, err)
		}
	}
	if len(sown.zones) == 0 {
		return fmt.Errorf("no %s growing zone opened for the %s deficit after %d windows: %#v", c.plant, c.product, phaseWindows, report["sow_census"])
	}
	if sown.sown() == 0 {
		return fmt.Errorf("%s zone opened but nothing was sown after %d windows: %#v", c.plant, phaseWindows, report["sow_census"])
	}
	if sown.wild != 0 {
		return fmt.Errorf("a wild %s source stood on the map (%d), so the field is not what served it", c.product, sown.wild)
	}

	// Phase 2: the matured crop is harvested into the product.
	var harvested fieldCensus
	for attempt := 1; attempt <= phaseWindows && harvested.units == 0; attempt++ {
		_, err := sustainedfood.Observe(ctx, s, sustainedfood.Observation{
			WatchConfig: sustainedfood.WatchConfig{Watch: 12 * time.Minute, Window: nonFoodWindow, Concern: c.concern},
			Audit: func(ctx context.Context, h *na.Harness, report na.Report) error {
				var reply map[string]any
				var err error
				if harvested, reply, err = c.census(ctx, h, fmt.Sprintf("harvest-census-%d", attempt)); err != nil {
					return err
				}
				report["harvest_census"] = reply
				return nil
			},
		})
		if err != nil {
			return fmt.Errorf("harvest window %d: %w", attempt, err)
		}
	}
	if harvested.units == 0 {
		return fmt.Errorf("the matured %s field yielded no %s after %d windows: %#v", c.plant, c.product, phaseWindows, report["harvest_census"])
	}
	return nil
}
