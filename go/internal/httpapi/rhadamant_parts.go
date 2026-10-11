package httpapi

import "github.com/davidarcher/RimGovernor/go/internal/policy"

// RhadamantParts are Rhadamant's Sea Ice Community Challenge (Workshop
// 1565580939, 1.0 format) as scenario parts: 800 silver, 50 survival meals,
// 300 wood, 450 steel, 30 components, 30 medicine and a rifle at the start,
// 720 steel, 7 meals and 3 ship chunks scattered, forced Sanguine,
// Super-Immune and Jogger traits and a permanent ColdSnap. Parts replace the
// base scenario's parts of their kind, so these lists are the whole of them.
// The launcher preset and the solo/sea-ice acceptance case both start from
// them. Her cannibalism rules are not included.
func RhadamantParts() NewColonyPartsDTO {
	d := policy.RhadamantThings
	f32 := func(v float32) *float32 { return &v }
	i32 := func(v int32) *int32 { return &v }
	u32 := func(v uint32) *uint32 { return &v }
	thing := func(placement, def string, n uint32) StartingThingDTO {
		return StartingThingDTO{Placement: placement, ThingDef: def, Count: u32(n)}
	}
	trait := func(def string, degree int32) ForcedTraitDTO {
		return ForcedTraitDTO{Trait: def, Degree: i32(degree), Chance: f32(1), Context: "player_starter"}
	}
	return NewColonyPartsDTO{
		StartingThings: []StartingThingDTO{
			thing("start_with", d.Silver, 800), thing("start_with", d.Meal, 50), thing("start_with", d.Wood, 300),
			thing("start_with", d.Steel, 450), thing("start_with", d.Component, 30),
			thing("start_with", d.Medicine, 30), thing("start_with", d.Rifle, 1),
			thing("scattered", d.Steel, 720), thing("scattered", d.Meal, 7), thing("scattered", d.ShipChunk, 3),
		},
		ForcedTraits:        []ForcedTraitDTO{trait("NaturalMood", 2), trait("Immunity", 1), trait("SpeedOffset", 2)},
		PermaGameConditions: []string{"ColdSnap"},
	}
}
