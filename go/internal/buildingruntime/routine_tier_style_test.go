package buildingruntime

import (
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

// The shell, floor and lamp styles fold the projection into the tier rules:
// a Camp (or unknown-tier) colony gets a wood shell and no floor or lamp; a
// Masonry colony a stone shell and stone floors with a torch; Industrial
// steel accents, a steel door, and an Autodoor only once the definition
// reads available on a powered colony (#610).
func TestTierStylesFollowTheProjection(t *testing.T) {
	stone := map[policy.Resource]int64{"WoodLog": 200, "BlocksGranite": 300, "Steel": 100}
	// Unknown tier: the Camp rung.
	unknown := observation.ColonyProjection{Resources: domain.Known(stone)}
	shell := shellStyle(unknown)
	if shell.DoorDef != "Door" || shell.DoorStuff != "WoodLog" || shell.WallStuff(domain.ShellCorner) != "WoodLog" {
		t.Fatalf("unknown tier shell %+v", shell)
	}
	if floorStyle(unknown) != nil || lampStyle(unknown) != "" {
		t.Fatal("unknown tier styled a floor or lamp")
	}
	// Camp with no stock: a wood Door the preview refuses on stock.
	camp := styledProjection(policy.BuildTierCamp, nil)
	if shell = shellStyle(camp); shell.DoorDef != "Door" || shell.DoorStuff != "WoodLog" || shell.WallStuff(domain.ShellRun) != "WoodLog" {
		t.Fatalf("camp shell %+v", shell)
	}
	// Masonry: the quarried stone everywhere, stone floors, a torch.
	masonry := styledProjection(policy.BuildTierMasonry, stone)
	shell = shellStyle(masonry)
	if shell.DoorDef != "Door" || shell.DoorStuff != "BlocksGranite" || shell.WallStuff(domain.ShellRun) != "BlocksGranite" || shell.WallStuff(domain.ShellCorner) != "BlocksGranite" {
		t.Fatalf("masonry shell %+v", shell)
	}
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
	// Industrial without power: steel corners and frames over stone runs,
	// a steel Door; the lamp rule's one rung down is still a powered rung,
	// so no lamp is styled and the lighting ladder decides.
	industrial := styledProjection(policy.BuildTierIndustrial, stone, "Autodoors")
	shell = shellStyle(industrial)
	if shell.DoorDef != "Door" || shell.DoorStuff != "Steel" || shell.WallStuff(domain.ShellRun) != "BlocksGranite" || shell.WallStuff(domain.ShellCorner) != "Steel" || shell.WallStuff(domain.ShellDoorFrame) != "Steel" {
		t.Fatalf("industrial shell %+v", shell)
	}
	if lamp := lampStyle(industrial); lamp != "" {
		t.Fatal(lamp)
	}
	// Powered, with the Autodoor definition read available: an Autodoor and
	// a standing lamp.
	industrial.PowerPlanning = domain.Known(policy.PowerTopology{Networks: []policy.PowerNetworkFact{{GenerationW: domain.Known(600.0)}}})
	industrial.Definitions = []observation.PlanningDefinition{{Name: "Autodoor", Available: domain.Known(true), ConstructionSkill: domain.Known[int32](0), Size: domain.Known(policy.Bounds{Width: 1, Height: 1})}}
	shell = shellStyle(industrial)
	if shell.DoorDef != "Autodoor" || shell.DoorStuff != "Steel" {
		t.Fatalf("powered industrial shell %+v", shell)
	}
	if lamp := lampStyle(industrial); lamp != "StandingLamp" {
		t.Fatal(lamp)
	}
	// The same colony before the Autodoor definition was read keeps the
	// plain steel Door.
	industrial.Definitions = nil
	if shell = shellStyle(industrial); shell.DoorDef != "Door" {
		t.Fatalf("autodoor proposed unread: %+v", shell)
	}
}
