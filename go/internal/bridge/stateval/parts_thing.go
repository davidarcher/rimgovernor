package stateval

import (
	"fmt"
	"slices"

	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
)

// The thing-group StatParts (epic #2621, #2639): parts that read a thing's
// own live state or the map around it. A definition request has no thing, so
// the game's `req.Thing` is null and the part leaves the value alone; a thing
// request states the facts in StatContext.Thing (context_thing.go). None
// overrides ForceShow.

func thingPartList() []Part {
	return []Part{
		partContentsBeauty{}, partCorpseCasket{}, partEnvironmentalEffects{}, partNearHarbingerTree{},
		partNoxiousHaze{}, statPartPollution{}, partToxicFallout{}, partHasRelic{}, partHealth{},
		partIsCorpseFresh{}, partMaxChanceIfRotting{}, partPlantGrowthNutritionFactor{},
		partWorkTableUnpowered{}, partArtificialBuildingsNearbyOffset{}, partBiocoded{},
		partUnfinishedThingIngredientsMass{},
	}
}

const (
	classBuildingCorpseCasket = "RimWorld.Building_CorpseCasket"
	classPlant                = "RimWorld.Plant"
	classUnfinishedThing      = "Verse.UnfinishedThing"
)

// thingContext is the request's thing context, nil for a definition request.
func thingContext(req *Request) *StatContext { return req.Subject.Context }

// thingClassIs is `req.Thing is <base>` for a thing request: the live thing's
// class is its def's thingClass.
func thingClassIs(req *Request, base string) (bool, error) {
	return req.Evaluator.classIsA(req.Thing.GetThingClass(), base)
}

// thingCompProps is the props row of the first comp of def whose class is
// assignable to base (ThingWithComps.GetComp<T>), nil when there is none.
func thingCompProps(req *Request, base string) (protoreflect.Message, error) {
	for i, opt := range req.Thing.GetComps() {
		comp := opt.GetValue()
		if comp == nil {
			return nil, fmt.Errorf("thing def %s has an empty comp at index %d", req.Subject.Def, i)
		}
		msg := comp.ProtoReflect()
		field := msg.WhichOneof(msg.Descriptor().Oneofs().ByName("value"))
		if field == nil {
			return nil, fmt.Errorf("thing def %s comp %d names no class", req.Subject.Def, i)
		}
		inner := msg.Get(field).Message()
		class := inner.Get(inner.Descriptor().Fields().ByName("compClass")).String()
		ok, err := req.Evaluator.classIsA(class, base)
		if err != nil {
			return nil, err
		}
		if ok {
			return inner, nil
		}
	}
	return nil, nil
}

// compField is a field of a comp props row, by name.
func compField(props protoreflect.Message, name string) (protoreflect.Value, error) {
	field := props.Descriptor().Fields().ByName(protoreflect.Name(name))
	if field == nil {
		return protoreflect.Value{}, fmt.Errorf("%s has no field %s", props.Descriptor().FullName(), name)
	}
	return props.Get(field), nil
}

// biotechOn is ModLister.CheckBiotech / ModsConfig.BiotechActive.
func biotechOn(req *Request) (bool, error) { return req.Evaluator.modActive(modBiotech) }

// --- StatPart_ContentsBeauty ---

// partContentsBeauty is StatPart_ContentsBeauty: add an IBeautyContainer's
// BeautyOffset.
type partContentsBeauty struct{ plain }

func (partContentsBeauty) Class() string { return "StatPart_ContentsBeauty" }

func (partContentsBeauty) Transform(req *Request, _ proto.Message, val float32) (float32, error) {
	ctx := thingContext(req)
	if ctx == nil {
		return val, nil
	}
	offset, err := need(ctx.Thing.BeautyOffset, "the thing's IBeautyContainer.BeautyOffset")
	if err != nil || offset == nil {
		return val, err
	}
	return float32(val + *offset), nil
}

// --- StatPart_CorpseCasket ---

// partCorpseCasket is StatPart_CorpseCasket: an offset while a corpse casket
// of the listed defs holds a corpse.
type partCorpseCasket struct{ plain }

func (partCorpseCasket) Class() string { return "StatPart_CorpseCasket" }

func (partCorpseCasket) Transform(req *Request, row proto.Message, val float32) (float32, error) {
	p, err := rowOf[*d.StatPart_CorpseCasket](row, "StatPart_CorpseCasket")
	if err != nil {
		return 0, err
	}
	ctx := thingContext(req)
	if ctx == nil {
		return val, nil
	}
	casket, err := thingClassIs(req, classBuildingCorpseCasket)
	if err != nil || !casket {
		return val, err
	}
	has, err := need(ctx.Thing.CorpseCasketHasCorpse, "whether the corpse casket holds a corpse")
	if err != nil || !has || p.GetOffsetOccupied() == 0 {
		return val, err
	}
	// An empty list is the game's null: every def.
	if list := p.GetThingDefs(); len(list) > 0 && !slices.Contains(list, req.Subject.Def) {
		return val, nil
	}
	return float32(val + float32(p.GetOffsetOccupied())), nil
}

// --- StatPart_EnvironmentalEffects ---

// partEnvironmentalEffects is StatPart_EnvironmentalEffects: the deterioration
// factor from roof, room, terrain, rain and edifice, which replaces the value
// by val times that factor.
type partEnvironmentalEffects struct{ plain }

func (partEnvironmentalEffects) Class() string { return "StatPart_EnvironmentalEffects" }

func (partEnvironmentalEffects) Transform(req *Request, row proto.Message, val float32) (float32, error) {
	p, err := rowOf[*d.StatPart_EnvironmentalEffects](row, "StatPart_EnvironmentalEffects")
	if err != nil {
		return 0, err
	}
	ctx := thingContext(req)
	if ctx == nil {
		return val, nil
	}
	spawned, err := need(ctx.Spawned, "whether the thing is spawned")
	if err != nil || !spawned || !req.Thing.GetDeteriorateFromEnvironmentalEffects() {
		return val, err
	}
	facts := &ctx.Thing
	roofed, err := need(ctx.Roofed, "whether the thing's cell is roofed")
	if err != nil {
		return 0, err
	}
	var num float32
	if !roofed {
		num = float32(num + p.GetFactorOffsetUnroofed())
	}
	outdoorTemp, err := need(facts.RoomUsesOutdoorTemperature, "whether the thing's room uses the outdoor temperature")
	if err != nil {
		return 0, err
	}
	if outdoorTemp {
		num = float32(num + p.GetFactorOffsetOutdoors())
	}
	terrain, err := need(facts.Terrain, "the terrain at the thing's cell")
	if err != nil {
		return 0, err
	}
	if terrain != "" {
		def := req.Evaluator.catalog.TerrainDef(terrain)
		if def == nil {
			return 0, fmt.Errorf("catalog has no terrain def %s", terrain)
		}
		if extra := def.GetExtraDeteriorationFactor(); extra != 0 {
			num = float32(num + extra)
		}
	}
	if !roofed {
		rain, err := need(facts.RainRate, "the map's rain rate")
		if err != nil {
			return 0, err
		}
		num = mul(num, lerp32(1, 5, rain))
	}
	protected, err := need(facts.ProtectedByEdifice, "whether an edifice protects the thing's cell from deterioration")
	if err != nil {
		return 0, err
	}
	if protected {
		num = mul(num, p.GetProtectedByEdificeFactor())
	}
	return mul(val, num), nil
}

// --- the multiplier parts ---

// partNearHarbingerTree is StatPart_NearHarbingerTree.
type partNearHarbingerTree struct{ plain }

func (partNearHarbingerTree) Class() string { return "StatPart_NearHarbingerTree" }

func (partNearHarbingerTree) Transform(req *Request, row proto.Message, val float32) (float32, error) {
	p, err := rowOf[*d.StatPart_NearHarbingerTree](row, "StatPart_NearHarbingerTree")
	if err != nil {
		return 0, err
	}
	ctx := thingContext(req)
	if ctx == nil {
		return val, nil
	}
	consumed, err := need(ctx.Thing.HarbingerBeingConsumed, "whether a harbinger tree is consuming the thing")
	if err != nil || !consumed {
		return val, err
	}
	return mul(val, p.GetMultiplier()), nil
}

// partNoxiousHaze is StatPart_NoxiousHaze (Biotech).
type partNoxiousHaze struct{ plain }

func (partNoxiousHaze) Class() string { return "StatPart_NoxiousHaze" }

func (partNoxiousHaze) Transform(req *Request, row proto.Message, val float32) (float32, error) {
	p, err := rowOf[*d.StatPart_NoxiousHaze](row, "StatPart_NoxiousHaze")
	if err != nil {
		return 0, err
	}
	ctx := thingContext(req)
	if ctx == nil {
		return val, nil
	}
	biotech, err := biotechOn(req)
	if err != nil || !biotech || !req.Thing.GetDeteriorateFromEnvironmentalEffects() {
		return val, err
	}
	exposed, err := exposedToNoxiousHaze(req, ctx)
	if err != nil || !exposed {
		return val, err
	}
	return mul(val, p.GetMultiplier()), nil
}

// exposedToNoxiousHaze is NoxiousHazeUtility.IsExposedToNoxiousHaze(thing)
// with Biotech active.
func exposedToNoxiousHaze(req *Request, ctx *StatContext) (bool, error) {
	f := &ctx.Thing.NoxiousHaze
	held, err := need(f.SpawnedOrAnyParentSpawned, "whether the thing or a parent is spawned")
	if err != nil || !held {
		return false, err
	}
	active, err := need(f.Active, "whether the map has an unhidden noxious haze condition")
	if err != nil || !active {
		return false, err
	}
	if ctx.Pawn != nil {
		immune, err := need(f.PawnImmune, "whether the pawn kind is immune to game condition effects")
		if err != nil || immune {
			return false, err
		}
	}
	roofed, err := need(f.HeldRoofed, "whether the thing's held cell is roofed")
	if err != nil {
		return false, err
	}
	if req.Thing.GetCategory() == d.ThingCategory_THING_CATEGORY_ITEM {
		return !roofed, nil
	}
	if !roofed {
		return true, nil
	}
	return need(f.HeldRoomPsychologicallyOutdoors, "whether the held cell's room is psychologically outdoors")
}

// statPartPollution is StatPart_Pollution (Biotech).
type statPartPollution struct{ plain }

func (statPartPollution) Class() string { return "StatPart_Pollution" }

func (statPartPollution) Transform(req *Request, row proto.Message, val float32) (float32, error) {
	p, err := rowOf[*d.StatPart_Pollution](row, "StatPart_Pollution")
	if err != nil {
		return 0, err
	}
	ctx := thingContext(req)
	if ctx == nil {
		return val, nil
	}
	biotech, err := biotechOn(req)
	if err != nil || !biotech {
		return val, err
	}
	spawned, err := need(ctx.Spawned, "whether the thing is spawned")
	if err != nil || !spawned || !req.Thing.GetDeteriorateFromEnvironmentalEffects() {
		return val, err
	}
	polluted, err := need(ctx.Thing.Polluted, "whether the thing's cell is polluted")
	if err != nil || !polluted {
		return val, err
	}
	return mul(val, p.GetMultiplier()), nil
}

// partToxicFallout is StatPart_ToxicFallout.
type partToxicFallout struct{ plain }

func (partToxicFallout) Class() string { return "StatPart_ToxicFallout" }

func (partToxicFallout) Transform(req *Request, row proto.Message, val float32) (float32, error) {
	p, err := rowOf[*d.StatPart_ToxicFallout](row, "StatPart_ToxicFallout")
	if err != nil {
		return 0, err
	}
	ctx := thingContext(req)
	if ctx == nil || !req.Thing.GetDeteriorateFromEnvironmentalEffects() {
		return val, nil
	}
	f := &ctx.Thing.ToxicFallout
	hasMap, err := need(f.HasMapHeld, "whether the thing is on a map")
	if err != nil || !hasMap {
		return val, err
	}
	active, err := need(f.Active, "whether toxic fallout is active on the thing's map")
	if err != nil || !active {
		return val, err
	}
	valid, err := need(f.PositionHeldValid, "whether the thing's held position is valid")
	if err != nil || !valid {
		return val, err
	}
	roofed, err := need(f.PositionHeldRoofed, "whether the thing's held cell is roofed")
	if err != nil || roofed {
		return val, err
	}
	return mul(val, p.GetMultiplier()), nil
}

// --- StatPart_HasRelic ---

// partHasRelic is StatPart_HasRelic: an offset while a relic container holds
// its relic.
type partHasRelic struct{ plain }

func (partHasRelic) Class() string { return "StatPart_HasRelic" }

func (partHasRelic) Transform(req *Request, row proto.Message, val float32) (float32, error) {
	p, err := rowOf[*d.StatPart_HasRelic](row, "StatPart_HasRelic")
	if err != nil {
		return 0, err
	}
	ctx := thingContext(req)
	if ctx == nil {
		return val, nil
	}
	has, err := need(ctx.Thing.RelicContained, "whether the thing's relic container holds a relic")
	if err != nil || !has {
		return val, err
	}
	return float32(val + p.GetOffset()), nil
}

// --- StatPart_Health (a StatPart_Curve) ---

// partHealth is StatPart_Health: multiply by the curve at HitPoints over
// MaxHitPoints for a def that uses hit points and has health affect price.
type partHealth struct{ plain }

func (partHealth) Class() string { return "StatPart_Health" }

func (partHealth) Transform(req *Request, row proto.Message, val float32) (float32, error) {
	p, err := rowOf[*d.StatPart_Health](row, "StatPart_Health")
	if err != nil {
		return 0, err
	}
	ctx := thingContext(req)
	if ctx == nil || !req.Thing.GetUseHitPoints() || !req.Thing.GetHealthAffectsPrice() {
		return val, nil
	}
	hp, err := need(ctx.Thing.HitPoints, "the thing's hit points")
	if err != nil {
		return 0, err
	}
	maxHP, err := need(ctx.Thing.MaxHitPoints, "the thing's max hit points")
	if err != nil {
		return 0, err
	}
	factor, err := EvaluateCurve(p.GetCurve(), float32(float32(hp)/float32(maxHP)))
	if err != nil {
		return 0, err
	}
	return mul(val, factor), nil
}

// --- rot ---

// partIsCorpseFresh is StatPart_IsCorpseFresh: a corpse that is not fresh
// counts for nothing.
type partIsCorpseFresh struct{ plain }

func (partIsCorpseFresh) Class() string { return "StatPart_IsCorpseFresh" }

func (partIsCorpseFresh) Transform(req *Request, _ proto.Message, val float32) (float32, error) {
	ctx := thingContext(req)
	if ctx == nil {
		return val, nil
	}
	corpse, err := thingClassIs(req, classCorpse)
	if err != nil || !corpse {
		return val, err
	}
	rot, err := need(ctx.Thing.Rot, "the corpse's rot stage")
	if err != nil {
		return 0, err
	}
	if rot == ThingRotFresh {
		return mul(val, 1), nil
	}
	return mul(val, 0), nil
}

// partMaxChanceIfRotting is StatPart_MaxChanceIfRotting: 1 once the thing is
// not fresh.
type partMaxChanceIfRotting struct{ plain }

func (partMaxChanceIfRotting) Class() string { return "StatPart_MaxChanceIfRotting" }

func (partMaxChanceIfRotting) Transform(req *Request, _ proto.Message, val float32) (float32, error) {
	ctx := thingContext(req)
	if ctx == nil {
		return val, nil
	}
	rot, err := need(ctx.Thing.Rot, "the thing's rot stage")
	if err != nil {
		return 0, err
	}
	if rot != ThingRotFresh {
		return 1, nil
	}
	return val, nil
}

// --- StatPart_PlantGrowthNutritionFactor ---

// partPlantGrowthNutritionFactor is StatPart_PlantGrowthNutritionFactor: a
// plant's nutrition scales with its growth (wild plants from 0.5).
type partPlantGrowthNutritionFactor struct{ plain }

func (partPlantGrowthNutritionFactor) Class() string { return "StatPart_PlantGrowthNutritionFactor" }

func (partPlantGrowthNutritionFactor) Transform(req *Request, _ proto.Message, val float32) (float32, error) {
	ctx := thingContext(req)
	if ctx == nil {
		return val, nil
	}
	plant, err := thingClassIs(req, classPlant)
	if err != nil || !plant {
		return val, err
	}
	growth, err := need(ctx.Thing.PlantGrowth, "the plant's growth")
	if err != nil {
		return 0, err
	}
	props := req.Thing.GetPlant()
	if props == nil {
		return 0, fmt.Errorf("plant %s has no plant properties", req.Subject.Def)
	}
	factor := growth // PlantUtility.NutritionFactorFromGrowth
	if len(props.GetSowTags()) == 0 {
		factor = lerp32(0.5, 1, growth)
	}
	return mul(val, factor), nil
}

// --- StatPart_WorkTableUnpowered ---

// partWorkTableUnpowered is StatPart_WorkTableUnpowered.
type partWorkTableUnpowered struct{ plain }

func (partWorkTableUnpowered) Class() string { return "StatPart_WorkTableUnpowered" }

func (partWorkTableUnpowered) Transform(req *Request, _ proto.Message, val float32) (float32, error) {
	ctx := thingContext(req)
	if ctx == nil {
		return val, nil
	}
	building := req.Thing.GetBuilding()
	if building == nil {
		return 0, fmt.Errorf("StatPart_WorkTableUnpowered reads the building properties of %s, which has none", req.Subject.Def)
	}
	factor := building.GetUnpoweredWorkTableWorkSpeedFactor()
	if factor == 0 {
		return val, nil
	}
	off, err := need(ctx.Thing.PowerTraderOff, "whether the thing has an unpowered CompPowerTrader")
	if err != nil || !off {
		return val, err
	}
	return mul(val, factor), nil
}

// --- StatPart_ArtificialBuildingsNearbyOffset ---

// partArtificialBuildingsNearbyOffset is StatPart_ArtificialBuildingsNearbyOffset:
// add the curve at the number of artificial buildings within the radius.
type partArtificialBuildingsNearbyOffset struct{ plain }

func (partArtificialBuildingsNearbyOffset) Class() string {
	return "StatPart_ArtificialBuildingsNearbyOffset"
}

func (partArtificialBuildingsNearbyOffset) Transform(req *Request, row proto.Message, val float32) (float32, error) {
	p, err := rowOf[*d.StatPart_ArtificialBuildingsNearbyOffset](row, "StatPart_ArtificialBuildingsNearbyOffset")
	if err != nil {
		return 0, err
	}
	ctx := thingContext(req)
	if ctx == nil {
		return val, nil
	}
	spawned, err := need(ctx.Spawned, "whether the thing is spawned")
	if err != nil || !spawned {
		return val, err
	}
	counts, err := need(ctx.Thing.ArtificialBuildingsNear, "the artificial buildings near the thing")
	if err != nil {
		return 0, err
	}
	count, ok := counts[p.GetRadius()]
	if !ok {
		return 0, fmt.Errorf("stat evaluation needs the artificial building count within radius %v: not observed", p.GetRadius())
	}
	offset, err := EvaluateCurve(p.GetCurve(), float32(count))
	if err != nil {
		return 0, err
	}
	return float32(val + offset), nil
}

// --- StatPart_Biocoded ---

// partBiocoded is StatPart_Biocoded: a biocoded thing is worth nothing.
type partBiocoded struct{ plain }

func (partBiocoded) Class() string { return "StatPart_Biocoded" }

func (partBiocoded) Transform(req *Request, _ proto.Message, val float32) (float32, error) {
	ctx := thingContext(req)
	if ctx == nil {
		return val, nil
	}
	coded, err := need(ctx.Thing.Biocoded, "whether the thing is biocoded")
	if err != nil || !coded {
		return val, err
	}
	return mul(val, 0), nil
}

// --- StatPart_UnfinishedThingIngredientsMass ---

// partUnfinishedThingIngredientsMass is StatPart_UnfinishedThingIngredientsMass.
type partUnfinishedThingIngredientsMass struct{ plain }

func (partUnfinishedThingIngredientsMass) Class() string {
	return "StatPart_UnfinishedThingIngredientsMass"
}

func (partUnfinishedThingIngredientsMass) Transform(req *Request, _ proto.Message, val float32) (float32, error) {
	ctx := thingContext(req)
	if ctx == nil {
		return val, nil
	}
	unfinished, err := thingClassIs(req, classUnfinishedThing)
	if err != nil || !unfinished {
		return val, err
	}
	ingredients, err := need(ctx.Thing.UnfinishedIngredients, "the unfinished thing's ingredients")
	if err != nil {
		return 0, err
	}
	var sum float32
	for _, ing := range ingredients {
		sum = float32(sum + float32(ing.Mass*float32(ing.StackCount)))
	}
	return float32(val + sum), nil
}
