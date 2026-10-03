package observation

import (
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func stuffedDef(name string, options ...StuffOption) PlanningDefinition {
	return PlanningDefinition{Name: name, Stuffed: true, StuffOptions: options}
}

func option(stuff string, units int64, value float64, stats map[string]float64) StuffOption {
	return StuffOption{Stuff: stuff, Costs: []policy.Amount{{Resource: policy.Resource(stuff), Count: units}}, Value: value, Stats: stats}
}

func TestStuffChoiceByCriterion(t *testing.T) {
	wall := stuffedDef("Wall",
		option("Steel", 5, 10, map[string]float64{bridge.StatMaxHitPoints: 300, bridge.StatFlammability: 0, bridge.StatBedRestEffectiveness: 0.5}),
		option("WoodLog", 5, 2.5, map[string]float64{bridge.StatMaxHitPoints: 200, bridge.StatFlammability: 1, bridge.StatBedRestEffectiveness: 0.8}),
		option("BlocksGranite", 5, 3, map[string]float64{bridge.StatMaxHitPoints: 480, bridge.StatFlammability: 0, bridge.StatBedRestEffectiveness: 0.3}),
	)
	stock := map[policy.Resource]int64{"Steel": 50, "WoodLog": 50, "BlocksGranite": 50}
	for _, tc := range []struct {
		criterion StuffCriterion
		want      string
	}{
		{CheapestStuff, "WoodLog"},
		{MaxHitPointsPerCost, "BlocksGranite"}, // 160 against 80 and 30 per value
		{LowestFlammability, "BlocksGranite"},  // ties with Steel at 0; the lower name wins
		{MaxRestEffectiveness, "WoodLog"},
	} {
		got, err := wall.StuffChoice(tc.criterion, stock)
		if err != nil || got.Stuff != tc.want || len(got.Costs) != 1 || got.Costs[0].Resource != policy.Resource(tc.want) {
			t.Errorf("%s: %+v, %v; want %s", tc.criterion, got, err, tc.want)
		}
	}
	// Only a stocked stuff is chosen; the unstocked choice ignores the stock.
	got, err := wall.StuffChoice(CheapestStuff, map[policy.Resource]int64{"Steel": 5})
	if err != nil || got.Stuff != "Steel" {
		t.Fatal("stocked stuff not chosen", got, err)
	}
	if got, err = wall.UnstockedStuffChoice(CheapestStuff); err != nil || got.Stuff != "WoodLog" {
		t.Fatal(got, err)
	}
}

func TestStuffChoiceRefusals(t *testing.T) {
	wall := stuffedDef("Wall", option("Steel", 5, 10, map[string]float64{bridge.StatMaxHitPoints: 300}))
	for name, stock := range map[string]map[policy.Resource]int64{"nil stock": nil, "short stock": {"Steel": 4}} {
		if _, err := wall.StuffChoice(CheapestStuff, stock); !errors.Is(err, ErrNoStuffInStock) {
			t.Errorf("%s: %v", name, err)
		}
	}
	// A criterion stat the game does not show is no-data, never a default.
	if _, err := wall.StuffChoice(LowestFlammability, map[policy.Resource]int64{"Steel": 5}); !errors.Is(err, ErrNoStuffData) {
		t.Error("an absent flammability chose a stuff", err)
	}
	if _, err := stuffedDef("Wall").StuffChoice(CheapestStuff, nil); !errors.Is(err, ErrNoStuffData) {
		t.Error("a stuffed def with no allowed stuff priced", err)
	}
	free := stuffedDef("Wall", option("Steel", 5, 0, map[string]float64{bridge.StatMaxHitPoints: 300}))
	if _, err := free.StuffChoice(MaxHitPointsPerCost, map[policy.Resource]int64{"Steel": 5}); !errors.Is(err, ErrNoStuffData) {
		t.Error("a zero cost value divided", err)
	}
}

func TestStuffChoiceUnstuffedDefIsPricedFromItsCosts(t *testing.T) {
	d := PlanningDefinition{Name: "Hopper", Costs: domain.Known([]policy.Amount{{Resource: "Steel", Count: 15}})}
	got, err := d.StuffChoice(MaxHitPointsPerCost, nil)
	if err != nil || got.Stuff != "" || len(got.Costs) != 1 || got.Costs[0].Count != 15 {
		t.Fatal(got, err)
	}
	if costs, known := d.CheapestCosts().Value(); !known || len(costs) != 1 {
		t.Fatal(costs, known)
	}
	if _, err = (PlanningDefinition{Name: "Hopper"}).StuffChoice(CheapestStuff, nil); !errors.Is(err, ErrNoStuffData) {
		t.Fatal("unknown costs priced", err)
	}
}

// A wall or door that holds a fire in is made of the least flammable stuff
// the game's rows offer, and a def whose every stuff burns is refused (#1814).
func TestFireproofStuff(t *testing.T) {
	burns := func(f float64) map[string]float64 { return map[string]float64{bridge.StatFlammability: f} }
	wall := stuffedDef("Wall", option("WoodLog", 5, 2.5, burns(1)), option("Steel", 5, 10, burns(0)), option("BlocksGranite", 5, 3, burns(0)))
	if got, err := wall.FireproofStuff(); err != nil || got != "BlocksGranite" {
		t.Fatal(got, err)
	}
	// Flammable wall or door material is rejected, wood and cloth alike.
	for name, def := range map[string]PlanningDefinition{
		"wood wall": stuffedDef("Wall", option("WoodLog", 5, 2.5, burns(1))),
		"wood door": stuffedDef("Door", option("WoodLog", 5, 2.5, burns(1)), option("Cloth", 5, 1, burns(1.2))),
		"scorched":  stuffedDef("Door", option("Leather", 5, 1, burns(0.4))),
	} {
		if got, err := def.FireproofStuff(); !errors.Is(err, ErrFlammableStuff) || got != "" {
			t.Errorf("%s: %q, %v", name, got, err)
		}
	}
	// No flammability shown, or a def with no stuff, is no data.
	for name, def := range map[string]PlanningDefinition{
		"no stat":   stuffedDef("Wall", option("Steel", 5, 10, map[string]float64{bridge.StatMaxHitPoints: 300})),
		"unstuffed": {Name: "Wall"},
	} {
		if _, err := def.FireproofStuff(); !errors.Is(err, ErrNoStuffData) {
			t.Errorf("%s: %v", name, err)
		}
	}
}

func TestSharedStuffAndBuildStuff(t *testing.T) {
	wall := stuffedDef("Wall", option("Steel", 5, 10, nil), option("WoodLog", 5, 2.5, nil))
	door := stuffedDef("Door", option("WoodLog", 25, 12.5, nil), option("Steel", 25, 50, nil))
	plank := stuffedDef("Plank", option("Steel", 1, 2, nil))
	if stuff, ok := SharedStuff(wall, door); !ok || stuff != "WoodLog" {
		t.Fatal(stuff, ok)
	}
	if _, ok := SharedStuff(wall, door, stuffedDef("Rock", option("BlocksGranite", 1, 1, nil))); ok {
		t.Fatal("defs with no common stuff shared one")
	}
	if stuff, ok := SharedStuff(PlanningDefinition{Name: "Conduit"}, PlanningDefinition{Name: "Hopper"}); !ok || stuff != "" {
		t.Fatal("unstuffed defs share the empty stuff", stuff, ok)
	}
	if _, ok := SharedStuff(PlanningDefinition{Name: "Conduit"}, wall); ok {
		t.Fatal("an unstuffed def shared a stuff with a stuffed one")
	}

	p := ColonyProjection{Definitions: []PlanningDefinition{wall, plank, {Name: "Conduit"}}}
	if got := p.BuildStuff("Wall"); got != "WoodLog" {
		t.Fatal("unknown stock builds the cheapest allowed", got)
	}
	p.Resources = domain.Known(map[policy.Resource]int64{"Steel": 100})
	if got := p.BuildStuff("Wall"); got != "Steel" {
		t.Fatal("the stocked stuff leads", got)
	}
	p.Resources = domain.Known(map[policy.Resource]int64{"Steel": 100, "WoodLog": 100})
	if got := p.BuildStuff("Wall"); got != "WoodLog" {
		t.Fatal("the cheaper stocked stuff leads", got)
	}
	if p.BuildStuff("Conduit") != "" || p.BuildStuff("Missing") != "" {
		t.Fatal("a def with no stuff builds from none")
	}
}
