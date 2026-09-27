package bridge

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// The BuildingPatchIntent kinds (#940): one settings change on one exact
// building -- a cooler or heater target, a bed's medical flag or prisoner
// use, a grower's crop, a claim. Native checks the building and the game's
// own rules live; applied means set, and the next building read confirms
// it (NativeBuildingTemperature.cs routes the arms).
func buildingPatch(thing string, change func(*o.BuildingPatchIntent)) (*o.Action, error) {
	if validID(thing) != nil {
		return nil, contract("building patch requires an exact building")
	}
	intent := &o.BuildingPatchIntent{ThingId: proto.String(thing)}
	change(intent)
	return &o.Action{Intent: &o.Action_BuildingPatch{BuildingPatch: intent}}, nil
}

func buildingTemperatureAction(action domain.Action) (*o.Action, error) {
	v, ok := action.BuildingTemperature()
	if !ok || math.IsNaN(v.Celsius()) || math.IsInf(v.Celsius(), 0) {
		return nil, contract("not a building temperature action")
	}
	return buildingPatch(v.Thing(), func(i *o.BuildingPatchIntent) {
		i.Change = &o.BuildingPatchIntent_TargetTemperature{TargetTemperature: float32(v.Celsius())}
	})
}

func bedUseAction(action domain.Action) (*o.Action, error) {
	v, ok := action.BedUse()
	if !ok {
		return nil, contract("not a bed use action")
	}
	return buildingPatch(v.Thing(), func(i *o.BuildingPatchIntent) {
		if v.Prisoners() {
			i.Change = &o.BuildingPatchIntent_ForPrisoners{ForPrisoners: &o.Clear{}}
		} else {
			i.Change = &o.BuildingPatchIntent_Medical{Medical: v.Medical()}
		}
	})
}

func growerCropAction(action domain.Action) (*o.Action, error) {
	v, ok := action.GrowerCrop()
	if !ok || validID(v.Crop()) != nil {
		return nil, contract("not a grower crop action")
	}
	return buildingPatch(v.Thing(), func(i *o.BuildingPatchIntent) {
		i.Change = &o.BuildingPatchIntent_PlantDef{PlantDef: v.Crop()}
	})
}

func claimBuildingAction(action domain.Action) (*o.Action, error) {
	v, ok := action.ClaimBuilding()
	if !ok {
		return nil, contract("not a claim building action")
	}
	return buildingPatch(v.Thing(), func(i *o.BuildingPatchIntent) { i.Change = &o.BuildingPatchIntent_Claim{Claim: &o.Clear{}} })
}
