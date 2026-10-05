package buildingruntime

import (
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func styledProjection(tier policy.BuildTier, stock map[policy.Resource]int64, finished ...string) observation.ColonyProjection {
	p := observation.ColonyProjection{BuildTier: domain.Known(tier), Resources: domain.Known(stock)}
	var research policy.ResearchFacts
	for _, name := range finished {
		research.Finished = append(research.Finished, policy.ResearchProjectID(name))
	}
	p.Facts.Research = domain.Known(research)
	return p
}

// The floor and lamp styles fold the projection into the tier rules: a Camp
// (or unknown-tier) colony gets no floor or lamp; a Masonry colony stone
// floors with a torch; Industrial a standing lamp only on a powered colony
// (#610).
func TestTierStylesFollowTheProjection(t *testing.T) {
	stone := map[policy.Resource]int64{"WoodLog": 200, "BlocksGranite": 300, "Steel": 100}
	// Unknown tier: the Camp rung.
	unknown := observation.ColonyProjection{Resources: domain.Known(stone)}
	if floorStyle(unknown) != nil || lampStyle(unknown) != "" {
		t.Fatal("unknown tier styled a floor or lamp")
	}
	// Masonry: stone floors and a torch.
	masonry := styledProjection(policy.BuildTierMasonry, stone)
	floor := floorStyle(masonry)
	if floor == nil {
		t.Fatal("masonry styled no floor")
	}
	if def, ok := floor(policy.RoomRoleBedroom); !ok || def != "TileGranite" {
		t.Fatal(def, ok)
	}
	if def, ok := floor(policy.RoomRoleNone); !ok || def != "FlagstoneGranite" {
		t.Fatal(def, ok)
	}
	if lamp := lampStyle(masonry); lamp != "TorchLamp" {
		t.Fatal(lamp)
	}
	// Industrial without power: the lamp rule's one rung down is still a
	// powered rung, so no lamp is styled and the lighting ladder decides.
	industrial := styledProjection(policy.BuildTierIndustrial, stone, "Autodoors")
	if lamp := lampStyle(industrial); lamp != "" {
		t.Fatal(lamp)
	}
	industrial.PowerPlanning = domain.Known(policy.PowerTopology{Networks: []policy.PowerNetworkFact{{GenerationW: domain.Known(600.0)}}})
	if lamp := lampStyle(industrial); lamp != "StandingLamp" {
		t.Fatal(lamp)
	}
}

// shellDefs are the Wall and Door rows the shell style ranks: wood is soft
// but opens doors fast, stone blocks are tough and slow, Bioferrite is cheap
// and tough but never an ordinary material.
func shellDefs() []observation.PlanningDefinition {
	option := func(stuff string, count int64, value, hp, speed float64, common bool) observation.StuffOption {
		stats := map[string]float64{bridge.StatMaxHitPoints: hp, bridge.StatFlammability: 0}
		if speed > 0 {
			stats[bridge.StatDoorOpenSpeed] = speed
		}
		return observation.StuffOption{Stuff: stuff, Costs: []policy.Amount{{Resource: policy.Resource(stuff), Count: count}}, Value: value, Common: common, Stats: stats}
	}
	build := func(name string, count int64, doorSpeed bool) observation.PlanningDefinition {
		speed := func(v float64) float64 {
			if doorSpeed {
				return v
			}
			return 0
		}
		return observation.PlanningDefinition{Name: name, Stuffed: true, NeedsPower: domain.Known(false), StuffOptions: []observation.StuffOption{
			option("Bioferrite", count, 0.75, 600, speed(1), false),
			option("BlocksGranite", count, 0.9, 510, speed(0.45), true),
			option("BlocksSandstone", count, 0.9, 420, speed(0.45), true),
			option("WoodLog", count, 1.2, 195, speed(1.2), true)}}
	}
	return []observation.PlanningDefinition{build("Wall", 5, false), build("Door", 25, true)}
}

func shellProjection(stock map[policy.Resource]int64) observation.ColonyProjection {
	p := styledProjection(policy.BuildTierCamp, stock)
	p.Definitions = shellDefs()
	return p
}

// The shell builds from the stuff the stock covers a whole shell of, ranked by
// the def stats: wood while it is all there is, stone once enough blocks are
// stocked, never Bioferrite, and a plain Door from wood throughout.
func TestShellStyleFollowsTheStock(t *testing.T) {
	budget := policy.ShellWallBudget
	for _, tc := range []struct {
		name         string
		stock        map[policy.Resource]int64
		wall, door   string
		noAcquisiton bool
	}{
		{"wood only", map[policy.Resource]int64{"WoodLog": 1000}, "WoodLog", "WoodLog", false},
		{"a few blocks never stall a wooden shell", map[policy.Resource]int64{"WoodLog": 1000, "BlocksGranite": 20}, "WoodLog", "WoodLog", false},
		{"enough blocks upgrade the walls", map[policy.Resource]int64{"WoodLog": 1000, "BlocksGranite": 5 * budget}, "BlocksGranite", "WoodLog", false},
		{"stone is all there is at camp", map[policy.Resource]int64{"WoodLog": 80, "BlocksSandstone": 5 * budget}, "BlocksSandstone", "WoodLog", true},
		{"a stocked exotic stuff never competes", map[policy.Resource]int64{"Bioferrite": 5 * budget, "WoodLog": 1000}, "WoodLog", "WoodLog", false},
	} {
		p := shellProjection(tc.stock)
		if tc.noAcquisiton {
			p.Acquisition = domain.Known([]policy.AcquisitionSource{})
		}
		if shell := shellStyle(p); shell.DoorDef != "Door" || shell.DoorStuff != tc.door || shell.WallStuff(domain.ShellRun) != tc.wall || shell.WallStuff(domain.ShellCorner) != tc.wall {
			t.Errorf("%s: %+v wall %s", tc.name, shell, shell.WallStuff(domain.ShellRun))
		}
	}
}

// A desert census (no trees, little wood) never plans a wood wall: with no
// blocks it names the best ordinary stone the stock check refuses, so the ring
// waits for quarrying.
func TestShellStyleOnAMapShortOfWood(t *testing.T) {
	desert := shellProjection(map[policy.Resource]int64{"WoodLog": 80})
	desert.Acquisition = domain.Known([]policy.AcquisitionSource{})
	if got := shellStyle(desert).WallStuff(domain.ShellRun); got == "WoodLog" || got == "Bioferrite" {
		t.Fatal("wood or exotic wall on a woodless map", got)
	}
}

// An Autodoor needs Autodoors finished, a generating source, the definition
// available and the stock to build one; it then ranks by durability (open
// speed only slows an unpowered door), so a stone Autodoor beats wood.
func TestShellStyleAutodoor(t *testing.T) {
	auto := shellDefs()[1]
	auto.Name, auto.Available, auto.NeedsPower = "Autodoor", domain.Known(true), domain.Known(true)
	auto.ConstructionSkill, auto.Size = domain.Known[int32](0), domain.Known(policy.Bounds{Width: 1, Height: 1})
	for i := range auto.StuffOptions {
		auto.StuffOptions[i].Costs = append(auto.StuffOptions[i].Costs, policy.Amount{Resource: "Steel", Count: 40})
	}
	p := styledProjection(policy.BuildTierIndustrial, map[policy.Resource]int64{"WoodLog": 1000, "BlocksGranite": 1000, "Steel": 100}, "Autodoors")
	p.Definitions = append(shellDefs(), auto)
	if shell := shellStyle(p); shell.DoorDef != "Door" {
		t.Fatalf("an Autodoor on an unpowered colony: %+v", shell)
	}
	p.PowerPlanning = domain.Known(policy.PowerTopology{Networks: []policy.PowerNetworkFact{{GenerationW: domain.Known(600.0)}}})
	if shell := shellStyle(p); shell.DoorDef != "Autodoor" || shell.DoorStuff != "BlocksGranite" {
		t.Fatalf("powered shell %+v", shell)
	}
	p.Resources = domain.Known(map[policy.Resource]int64{"WoodLog": 1000, "BlocksGranite": 1000, "Steel": 30})
	if shell := shellStyle(p); shell.DoorDef != "Door" {
		t.Fatalf("an Autodoor short of steel: %+v", shell)
	}
}
