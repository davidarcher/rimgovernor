package stateval

import (
	"fmt"

	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	"google.golang.org/protobuf/proto"
)

// The state-reading StatParts (epic #2621, #2638): pawn age, needs, pain,
// room, glow, temperature and outdoors. Each reads its row's fields and the
// request's StatContext (context.go). A definition request has no thing, so
// the game's StatRequest.HasThing is false and the part leaves the value alone;
// a thing request without a fact the part reads is an error naming the fact.
// None overrides ForceShow.

func statePartList() []Part {
	return []Part{
		partAge{}, partAgeOffset{}, partGlow{}, partOutdoors{}, partRest{}, partFood{},
		partPain{}, partMood{}, partResting{}, partRoomStat{}, partWorkTableOutdoors{},
		partWorkTableTemperature{}, partWorkTableRoomRole{}, partSlave{}, partDeathresting{},
		partMalnutrition{}, partFertilityByGenderAge{}, partFertilityByHediffs{}, partWildManOffset{},
		partGenes{},
	}
}

// plain is the default ForceShow.
type plain struct{}

func (plain) ForceShow(*Request, proto.Message) (bool, error) { return false, nil }

func mul(val, factor float32) float32 { return float32(val * factor) }

// race is the requested pawn def's RaceProperties.
func race(req *Request) (*d.RaceProperties, error) {
	r := req.Thing.GetRace()
	if r == nil {
		return nil, fmt.Errorf("thing %s has no race row", req.Subject.Def)
	}
	return r, nil
}

// humanlikeDef is RaceProperties.Humanlike of the requested def.
func humanlikeDef(req *Request) (bool, error) {
	r, err := race(req)
	if err != nil {
		return false, err
	}
	return humanlike(r), nil
}

// hasBuilding is ThingDef.building != null.
func hasBuilding(req *Request) bool { return req.Thing.GetBuilding() != nil }

// --- age ---

// ageValue is the age curve's value for the pawn, or active=false when the
// part does not apply (no pawn, no age tracker, or humanlikeOnly on a
// non-humanlike race).
func ageValue(req *Request, curve *d.SimpleCurve, useBiologicalYears, humanlikeOnly bool) (v float32, active bool, err error) {
	pawn := req.pawn()
	if pawn == nil {
		return 0, false, nil
	}
	age, err := need(pawn.Age, "the pawn's age tracker")
	if err != nil {
		return 0, false, err
	}
	if !age.Tracked {
		return 0, false, nil
	}
	if humanlikeOnly {
		h, err := humanlikeDef(req)
		if err != nil || !h {
			return 0, false, err
		}
	}
	x := float32(age.years())
	if !useBiologicalYears {
		r, err := race(req)
		if err != nil {
			return 0, false, err
		}
		x = float32(x / r.GetLifeExpectancy())
	}
	v, err = EvaluateCurve(curve, x)
	return v, err == nil, err
}

// partAge is StatPart_Age: multiply by the age curve.
type partAge struct{ plain }

func (partAge) Class() string { return "StatPart_Age" }

func (partAge) Transform(req *Request, row proto.Message, val float32) (float32, error) {
	p, err := rowOf[*d.StatPart_Age](row, "StatPart_Age")
	if err != nil {
		return 0, err
	}
	v, active, err := ageValue(req, p.GetCurve(), p.GetUseBiologicalYears(), p.GetHumanlikeOnly())
	if err != nil || !active {
		return val, err
	}
	return mul(val, v), nil
}

// partAgeOffset is StatPart_AgeOffset: add the age curve.
type partAgeOffset struct{ plain }

func (partAgeOffset) Class() string { return "StatPart_AgeOffset" }

func (partAgeOffset) Transform(req *Request, row proto.Message, val float32) (float32, error) {
	p, err := rowOf[*d.StatPart_AgeOffset](row, "StatPart_AgeOffset")
	if err != nil {
		return 0, err
	}
	v, active, err := ageValue(req, p.GetCurve(), p.GetUseBiologicalYears(), p.GetHumanlikeOnly())
	if err != nil || !active {
		return val, err
	}
	return float32(val + v), nil
}

// partFertilityByGenderAge is StatPart_FertilityByGenderAge: the gender's
// age curve at the biological age in fractional years.
type partFertilityByGenderAge struct{ plain }

func (partFertilityByGenderAge) Class() string { return "StatPart_FertilityByGenderAge" }

func (partFertilityByGenderAge) Transform(req *Request, row proto.Message, val float32) (float32, error) {
	p, err := rowOf[*d.StatPart_FertilityByGenderAge](row, "StatPart_FertilityByGenderAge")
	if err != nil {
		return 0, err
	}
	pawn := req.pawn()
	if pawn == nil {
		return val, nil
	}
	age, err := need(pawn.Age, "the pawn's age tracker")
	if err != nil {
		return 0, err
	}
	if !age.Tracked {
		return 0, fmt.Errorf("StatPart_FertilityByGenderAge reads the age tracker of a pawn that has none")
	}
	female, err := need(pawn.Female, "the pawn's gender")
	if err != nil {
		return 0, err
	}
	curve := p.GetMaleFertilityAgeFactor()
	if female {
		curve = p.GetFemaleFertilityAgeFactor()
	}
	factor, err := EvaluateCurve(curve, age.yearsFloat())
	if err != nil {
		return 0, err
	}
	return mul(val, factor), nil
}

// --- glow, outdoors, room ---

// partGlow is StatPart_Glow: multiply by the curve at the cell's glow.
type partGlow struct{ plain }

func (partGlow) Class() string { return "StatPart_Glow" }

func (partGlow) Transform(req *Request, row proto.Message, val float32) (float32, error) {
	p, err := rowOf[*d.StatPart_Glow](row, "StatPart_Glow")
	if err != nil {
		return 0, err
	}
	ctx := req.Subject.Context
	if ctx == nil {
		return val, nil
	}
	if pawn := ctx.Pawn; pawn != nil {
		if p.GetHumanlikeOnly() {
			if h, err := humanlikeDef(req); err != nil || !h {
				return val, err
			}
		}
		shambler, err := need(pawn.Shambler, "whether the pawn is a shambler")
		if err != nil || shambler {
			return val, err
		}
		if p.GetIgnoreIfIncapableOfSight() {
			blind, err := need(pawn.Blind, "whether the pawn is blind")
			if err != nil || blind {
				return val, err
			}
		}
		if p.GetIgnoreIfPrefersDarkness() {
			prefers, err := need(pawn.IdeoPrefersDarkness, "whether the pawn's ideo prefers darkness")
			if err != nil || prefers {
				return val, err
			}
			affected, err := need(pawn.GenesAffectedByDarkness, "whether the pawn's genes are affected by darkness")
			if err != nil || !affected {
				return val, err
			}
		}
	} else if p.GetPawnOnly() {
		return val, nil
	}
	spawned, err := need(ctx.Spawned, "whether the thing is spawned")
	if err != nil || !spawned {
		return val, err
	}
	glow, err := need(ctx.GlowLevel, "the glow level at the thing's cell")
	if err != nil {
		return 0, err
	}
	factor, err := EvaluateCurve(p.GetFactorFromGlowCurve(), glow)
	if err != nil {
		return 0, err
	}
	return mul(val, factor), nil
}

// partOutdoors is StatPart_Outdoors: the indoors or outdoors factor. A
// definition request is indoors.
type partOutdoors struct{ plain }

func (partOutdoors) Class() string { return "StatPart_Outdoors" }

func (partOutdoors) Transform(req *Request, row proto.Message, val float32) (float32, error) {
	p, err := rowOf[*d.StatPart_Outdoors](row, "StatPart_Outdoors")
	if err != nil {
		return 0, err
	}
	outdoors, err := consideredOutdoors(req)
	if err != nil {
		return 0, err
	}
	if outdoors {
		return mul(val, p.GetFactorOutdoors()), nil
	}
	return mul(val, p.GetFactorIndoors()), nil
}

func consideredOutdoors(req *Request) (bool, error) {
	ctx := req.Subject.Context
	if ctx == nil {
		return false, nil
	}
	room, err := need(ctx.Room, "the thing's room")
	if err != nil || room == nil {
		return false, err
	}
	forWork, err := need(room.OutdoorsForWork, "the room's OutdoorsForWork")
	if err != nil || forWork {
		return forWork, err
	}
	spawned, err := need(ctx.Spawned, "whether the thing is spawned")
	if err != nil || !spawned {
		return false, err
	}
	roofed, err := need(ctx.Roofed, "whether the thing's cell is roofed")
	return !roofed, err
}

// partRoomStat is StatPart_RoomStat: multiply by the thing's room's stat.
type partRoomStat struct{ plain }

func (partRoomStat) Class() string { return "StatPart_RoomStat" }

func (partRoomStat) Transform(req *Request, row proto.Message, val float32) (float32, error) {
	p, err := rowOf[*d.StatPart_RoomStat](row, "StatPart_RoomStat")
	if err != nil {
		return 0, err
	}
	ctx := req.Subject.Context
	if ctx == nil {
		return val, nil
	}
	room, err := need(ctx.Room, "the thing's room")
	if err != nil || room == nil {
		return val, err
	}
	stat, ok := room.Stats[p.GetRoomStat()]
	if !ok {
		return 0, fmt.Errorf("stat evaluation needs room stat %s: not observed", p.GetRoomStat())
	}
	return mul(val, stat), nil
}

// partWorkTableOutdoors is StatPart_WorkTableOutdoors: x0.8 for a building in
// a psychologically outdoor room.
type partWorkTableOutdoors struct{ plain }

func (partWorkTableOutdoors) Class() string { return "StatPart_WorkTableOutdoors" }

func (partWorkTableOutdoors) Transform(req *Request, _ proto.Message, val float32) (float32, error) {
	ctx := req.Subject.Context
	if ctx == nil || !hasBuilding(req) {
		return val, nil
	}
	spawned, err := need(ctx.Spawned, "whether the thing is spawned")
	if err != nil || !spawned { // an unspawned thing has no map
		return val, err
	}
	room, err := need(ctx.Room, "the thing's room")
	if err != nil || room == nil {
		return val, err
	}
	outdoors, err := need(room.PsychologicallyOutdoors, "the room's PsychologicallyOutdoors")
	if err != nil || !outdoors {
		return val, err
	}
	return mul(val, 0.8), nil
}

// partWorkTableTemperature is StatPart_WorkTableTemperature: x0.7 for a
// spawned building outside 9..35 C.
type partWorkTableTemperature struct{ plain }

func (partWorkTableTemperature) Class() string { return "StatPart_WorkTableTemperature" }

func (partWorkTableTemperature) Transform(req *Request, _ proto.Message, val float32) (float32, error) {
	ctx := req.Subject.Context
	if ctx == nil {
		return val, nil
	}
	spawned, err := need(ctx.Spawned, "whether the thing is spawned")
	if err != nil || !spawned || !hasBuilding(req) {
		return val, err
	}
	temp, err := need(ctx.Temperature, "the temperature at the thing's cell")
	if err != nil {
		return 0, err
	}
	if temp < 9 || temp > 35 {
		return mul(val, 0.7), nil
	}
	return val, nil
}

// partWorkTableRoomRole is StatPart_WorkTableRoomRole: the building's
// not-in-role factor when its room is indoors with another role.
type partWorkTableRoomRole struct{ plain }

func (partWorkTableRoomRole) Class() string { return "StatPart_WorkTableRoomRole" }

func (partWorkTableRoomRole) Transform(req *Request, _ proto.Message, val float32) (float32, error) {
	ctx := req.Subject.Context
	if ctx == nil || !hasBuilding(req) {
		return val, nil
	}
	building := req.Thing.GetBuilding()
	if building.GetWorkTableRoomRole() == "" {
		return val, nil
	}
	room, err := need(ctx.Room, "the thing's room")
	if err != nil || room == nil {
		return val, err
	}
	outdoors, err := need(room.PsychologicallyOutdoors, "the room's PsychologicallyOutdoors")
	if err != nil || outdoors {
		return val, err
	}
	role, err := need(room.Role, "the room's role")
	if err != nil || role == building.GetWorkTableRoomRole() {
		return val, err
	}
	return mul(val, building.GetWorkTableNotInRoomRoleFactor()), nil
}

// --- pawn needs and health ---

// needFactor is the pawn's need category looked up in the row's four factors
// (best to worst category order as given), or active=false without the need.
func needFactor(k Known[NeedState], what string, factors map[string]float32) (float32, bool, error) {
	n, err := need(k, what)
	if err != nil || !n.Present {
		return 0, false, err
	}
	f, ok := factors[n.Category]
	if !ok {
		return 0, false, fmt.Errorf("%s category %q is not a category", what, n.Category)
	}
	return f, true, nil
}

// partRest is StatPart_Rest.
type partRest struct{ plain }

func (partRest) Class() string { return "StatPart_Rest" }

func (partRest) Transform(req *Request, row proto.Message, val float32) (float32, error) {
	p, err := rowOf[*d.StatPart_Rest](row, "StatPart_Rest")
	if err != nil {
		return 0, err
	}
	pawn := req.pawn()
	if pawn == nil {
		return val, nil
	}
	f, active, err := needFactor(pawn.Rest, "the pawn's rest need", map[string]float32{
		RestExhausted: p.GetFactorExhausted(), RestVeryTired: p.GetFactorVeryTired(),
		RestTired: p.GetFactorTired(), RestRested: p.GetFactorRested(),
	})
	if err != nil || !active {
		return val, err
	}
	return mul(val, f), nil
}

// partFood is StatPart_Food.
type partFood struct{ plain }

func (partFood) Class() string { return "StatPart_Food" }

func (partFood) Transform(req *Request, row proto.Message, val float32) (float32, error) {
	p, err := rowOf[*d.StatPart_Food](row, "StatPart_Food")
	if err != nil {
		return 0, err
	}
	pawn := req.pawn()
	if pawn == nil {
		return val, nil
	}
	f, active, err := needFactor(pawn.Food, "the pawn's food need", map[string]float32{
		FoodStarving: p.GetFactorStarving(), FoodUrgentlyHungry: p.GetFactorUrgentlyHungry(),
		FoodHungry: p.GetFactorHungry(), FoodFed: p.GetFactorFed(),
	})
	if err != nil || !active {
		return val, err
	}
	return mul(val, f), nil
}

// partPain is StatPart_Pain: 1 + painTotal * factor.
type partPain struct{ plain }

func (partPain) Class() string { return "StatPart_Pain" }

func (partPain) Transform(req *Request, row proto.Message, val float32) (float32, error) {
	p, err := rowOf[*d.StatPart_Pain](row, "StatPart_Pain")
	if err != nil {
		return 0, err
	}
	pawn := req.pawn()
	if pawn == nil {
		return val, nil
	}
	pain, err := need(pawn.PainTotal, "the pawn's total pain")
	if err != nil {
		return 0, err
	}
	return mul(val, float32(1+float32(pain*p.GetFactor()))), nil
}

// partMood is StatPart_Mood: the curve at the mood level.
type partMood struct{ plain }

func (partMood) Class() string { return "StatPart_Mood" }

func (partMood) Transform(req *Request, row proto.Message, val float32) (float32, error) {
	p, err := rowOf[*d.StatPart_Mood](row, "StatPart_Mood")
	if err != nil {
		return 0, err
	}
	pawn := req.pawn()
	if pawn == nil {
		return val, nil
	}
	mood, err := need(pawn.Mood, "the pawn's mood need")
	if err != nil || !mood.Present {
		return val, err
	}
	factor, err := EvaluateCurve(p.GetFactorFromMoodCurve(), mood.Level)
	if err != nil {
		return 0, err
	}
	return mul(val, factor), nil
}

// partMalnutrition is StatPart_Malnutrition: the curve at the Malnutrition
// hediff's severity.
type partMalnutrition struct{ plain }

func (partMalnutrition) Class() string { return "StatPart_Malnutrition" }

func (partMalnutrition) Transform(req *Request, row proto.Message, val float32) (float32, error) {
	p, err := rowOf[*d.StatPart_Malnutrition](row, "StatPart_Malnutrition")
	if err != nil {
		return 0, err
	}
	pawn := req.pawn()
	if pawn == nil {
		return val, nil
	}
	m, err := need(pawn.Malnutrition, "the pawn's Malnutrition hediff")
	if err != nil || !m.Present {
		return val, err
	}
	factor, err := EvaluateCurve(p.GetCurve(), m.Severity)
	if err != nil {
		return 0, err
	}
	return mul(val, factor), nil
}

// partFertilityByHediffs is StatPart_FertilityByHediffs: the product of the
// hediffs' stage fertility factors, floored at 0.
type partFertilityByHediffs struct{ plain }

func (partFertilityByHediffs) Class() string { return "StatPart_FertilityByHediffs" }

func (partFertilityByHediffs) Transform(req *Request, _ proto.Message, val float32) (float32, error) {
	pawn := req.pawn()
	if pawn == nil {
		return val, nil
	}
	factors, err := need(pawn.FertilityFactors, "the pawn's hediff fertility factors")
	if err != nil {
		return 0, err
	}
	product := float32(1)
	for _, f := range factors {
		if f != 1 {
			product = mul(product, f)
		}
	}
	if product < 0 { // Mathf.Max(num, 0)
		product = 0
	}
	return mul(val, product), nil
}

// partResting is StatPart_Resting.
type partResting struct{ plain }

func (partResting) Class() string { return "StatPart_Resting" }

func (partResting) Transform(req *Request, row proto.Message, val float32) (float32, error) {
	p, err := rowOf[*d.StatPart_Resting](row, "StatPart_Resting")
	if err != nil {
		return 0, err
	}
	pawn := req.pawn()
	if pawn == nil {
		return val, nil
	}
	r, err := need(pawn.Resting, "whether the pawn is resting")
	if err != nil {
		return 0, err
	}
	if r.InBed || (r.NotStanding && !r.Downed) || (r.CaravanMember && !r.CaravanMoving) || r.InCaravanBed || r.CarriedByCaravan {
		return mul(val, p.GetFactor()), nil
	}
	return val, nil
}

// pawnFlag is the shape of Slave, Deathresting and WildManOffset: a boolean
// pawn fact picks a factor or an offset.
func pawnFlag(req *Request, pick func(*PawnState) Known[bool], what string) (bool, error) {
	pawn := req.pawn()
	if pawn == nil {
		return false, nil
	}
	return need(pick(pawn), what)
}

// partSlave is StatPart_Slave.
type partSlave struct{ plain }

func (partSlave) Class() string { return "StatPart_Slave" }

func (partSlave) Transform(req *Request, row proto.Message, val float32) (float32, error) {
	p, err := rowOf[*d.StatPart_Slave](row, "StatPart_Slave")
	if err != nil {
		return 0, err
	}
	slave, err := pawnFlag(req, func(s *PawnState) Known[bool] { return s.IsSlave }, "whether the pawn is a slave")
	if err != nil || !slave {
		return val, err
	}
	return mul(val, p.GetFactor()), nil
}

// partDeathresting is StatPart_Deathresting.
type partDeathresting struct{ plain }

func (partDeathresting) Class() string { return "StatPart_Deathresting" }

func (partDeathresting) Transform(req *Request, row proto.Message, val float32) (float32, error) {
	p, err := rowOf[*d.StatPart_Deathresting](row, "StatPart_Deathresting")
	if err != nil {
		return 0, err
	}
	resting, err := pawnFlag(req, func(s *PawnState) Known[bool] { return s.Deathresting }, "whether the pawn is deathresting")
	if err != nil || !resting {
		return val, err
	}
	return mul(val, p.GetFactor()), nil
}

// partWildManOffset is StatPart_WildManOffset.
type partWildManOffset struct{ plain }

func (partWildManOffset) Class() string { return "StatPart_WildManOffset" }

func (partWildManOffset) Transform(req *Request, row proto.Message, val float32) (float32, error) {
	p, err := rowOf[*d.StatPart_WildManOffset](row, "StatPart_WildManOffset")
	if err != nil {
		return 0, err
	}
	wild, err := pawnFlag(req, func(s *PawnState) Known[bool] { return s.IsWildMan }, "whether the pawn is a wild man")
	if err != nil || !wild {
		return val, err
	}
	return float32(val + p.GetOffset()), nil
}

// partGenes is StatPart_Genes: a Genepack's market value by gene count,
// archites and the genes' own factors (Biotech only).
type partGenes struct{ plain }

func (partGenes) Class() string { return "StatPart_Genes" }

func (partGenes) Transform(req *Request, _ proto.Message, val float32) (float32, error) {
	ctx := req.Subject.Context
	if ctx == nil {
		return val, nil
	}
	biotech, err := req.Evaluator.modActive("ludeon.rimworld.biotech")
	if err != nil || !biotech {
		return val, err
	}
	set, err := need(ctx.Genepack, "the thing's gene set")
	if err != nil || set == nil {
		return val, err
	}
	count := float32(len(set.MarketValueFactors))
	num := float32(3.5) - float32(0.5*count)
	if num < 0.5 { // Mathf.Max(3.5 - 0.5 * count, 0.5)
		num = 0.5
	}
	val = mul(val, num)
	val = mul(val, float32(1+float32(3*float32(set.ArchitesTotal))))
	factors := float32(1)
	for _, f := range set.MarketValueFactors {
		factors = mul(factors, f)
	}
	return mul(val, factors), nil
}
