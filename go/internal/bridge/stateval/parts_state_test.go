package stateval

import (
	"strings"
	"testing"

	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
)

// wrapPart is the StatPartAny entry holding row, whatever its class.
func wrapPart(t *testing.T, row proto.Message) *d.Opt_StatPartAny {
	t.Helper()
	any := &d.StatPartAny{}
	msg := any.ProtoReflect()
	fields := msg.Descriptor().Oneofs().ByName("value").Fields()
	for i := 0; i < fields.Len(); i++ {
		if f := fields.Get(i); f.Message().FullName() == row.ProtoReflect().Descriptor().FullName() {
			msg.Set(f, protoreflect.ValueOfMessage(row.ProtoReflect()))
			return &d.Opt_StatPartAny{Value: any}
		}
	}
	t.Fatalf("no StatPartAny member for %T", row)
	return nil
}

// stateCase is one part over one context. ctx nil is a definition request.
type stateCase struct {
	name    string
	row     proto.Message
	ctx     *StatContext
	animal  bool
	biotech bool
	want    float32
	err     string // substring of the expected error
}

func pawnCtx(edit func(*PawnState)) *StatContext {
	p := &PawnState{}
	edit(p)
	return &StatContext{Pawn: p}
}

func runStateCases(t *testing.T, cases []stateCase) {
	t.Helper()
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			r := newRig(t, func(stat *d.StatDef, parka, _ *d.ThingDef) {
				stat.Parts = []*d.Opt_StatPartAny{wrapPart(t, c.row)}
				intelligence := d.Intelligence_INTELLIGENCE_HUMANLIKE
				if c.animal {
					intelligence = d.Intelligence_INTELLIGENCE_ANIMAL
				}
				parka.Race = &d.RaceProperties{Intelligence: intelligence, LifeExpectancy: 80}
				parka.Building = &d.BuildingProperties{WorkTableRoomRole: "Workshop", WorkTableNotInRoomRoleFactor: 0.5}
			})
			if c.biotech {
				r.eval.env.ActiveMods["ludeon.rimworld.biotech"] = true
			}
			subject := ThingSubject("Apparel_Parka", "")
			subject.Context = c.ctx
			req, err := r.eval.request(testStat, subject)
			if err != nil {
				t.Fatal(err)
			}
			got, err := r.eval.finalize(req, 10)
			if c.err != "" {
				if err == nil || !strings.Contains(err.Error(), c.err) {
					t.Fatalf("error = %v, want one containing %q", err, c.err)
				}
				return
			}
			if err != nil {
				t.Fatal(err)
			}
			if got != c.want {
				t.Errorf("= %v, want %v", got, c.want)
			}
		})
	}
}

func ageAt(years int64) Known[AgeState] {
	return Some(AgeState{Tracked: true, BiologicalTicks: years * ticksPerYear})
}

func TestStatePartsAge(t *testing.T) {
	byLife := &d.StatPart_Age{Curve: curve(0, 1, 1, 3)} // years / lifeExpectancy 80
	byYears := &d.StatPart_Age{Curve: curve(0, 1, 80, 3), UseBiologicalYears: true}
	offset := &d.StatPart_AgeOffset{Curve: curve(0, 1, 80, 3), UseBiologicalYears: true}
	at40 := pawnCtx(func(p *PawnState) { p.Age = ageAt(40) })
	runStateCases(t, []stateCase{
		{name: "life fraction", row: byLife, ctx: at40, want: 20},
		{name: "biological years", row: byYears, ctx: at40, want: 20},
		{name: "a definition request has no pawn", row: byLife, want: 10},
		{name: "a non-pawn thing", row: byLife, ctx: &StatContext{}, want: 10},
		{name: "no age tracker", row: byLife, ctx: pawnCtx(func(p *PawnState) { p.Age = Some(AgeState{}) }), want: 10},
		{name: "humanlikeOnly on an animal", row: &d.StatPart_Age{Curve: curve(0, 1, 80, 3), HumanlikeOnly: true}, animal: true, ctx: at40, want: 10},
		{name: "humanlikeOnly on a human", row: &d.StatPart_Age{Curve: curve(0, 1, 80, 3), UseBiologicalYears: true, HumanlikeOnly: true}, ctx: at40, want: 20},
		{name: "age not observed", row: byLife, ctx: pawnCtx(func(*PawnState) {}), err: "age tracker"},
		{name: "offset", row: offset, ctx: at40, want: 12},
		{name: "offset on a definition request", row: offset, want: 10},
		{name: "age uses whole years", row: byYears, ctx: pawnCtx(func(p *PawnState) {
			p.Age = Some(AgeState{Tracked: true, BiologicalTicks: 40*ticksPerYear + ticksPerYear - 1})
		}), want: 20},
	})
}

func TestStatePartsFertility(t *testing.T) {
	age := &d.StatPart_FertilityByGenderAge{MaleFertilityAgeFactor: curve(0, 1, 100, 1), FemaleFertilityAgeFactor: curve(0, 0, 100, 1)}
	at := func(female bool) *StatContext {
		return pawnCtx(func(p *PawnState) { p.Age = ageAt(50); p.Female = Some(female) })
	}
	hediffs := func(f ...float32) *StatContext {
		return pawnCtx(func(p *PawnState) { p.FertilityFactors = Some(f) })
	}
	runStateCases(t, []stateCase{
		{name: "male curve", row: age, ctx: at(false), want: 10},
		{name: "female curve", row: age, ctx: at(true), want: 5},
		{name: "definition request", row: age, want: 10},
		{name: "gender not observed", row: age, ctx: pawnCtx(func(p *PawnState) { p.Age = ageAt(50) }), err: "gender"},
		{name: "hediff factors multiply", row: &d.StatPart_FertilityByHediffs{}, ctx: hediffs(0.5, 1, 0.5), want: 2.5},
		{name: "no hediffs", row: &d.StatPart_FertilityByHediffs{}, ctx: hediffs(), want: 10},
		{name: "negative product floors at zero", row: &d.StatPart_FertilityByHediffs{}, ctx: hediffs(-2), want: 0},
		{name: "hediffs not observed", row: &d.StatPart_FertilityByHediffs{}, ctx: pawnCtx(func(*PawnState) {}), err: "fertility"},
	})
}

func TestStatePartsGlow(t *testing.T) {
	row := func(edit func(*d.StatPart_Glow)) *d.StatPart_Glow {
		g := &d.StatPart_Glow{FactorFromGlowCurve: curve(0, 0.5, 1, 1)}
		edit(g)
		return g
	}
	pawn := func(edit func(*PawnState)) *StatContext {
		c := pawnCtx(func(p *PawnState) {
			p.Shambler, p.Blind = Some(false), Some(false)
			p.IdeoPrefersDarkness, p.GenesAffectedByDarkness = Some(false), Some(true)
			edit(p)
		})
		c.Spawned, c.GlowLevel = Some(true), Some(float32(0.5))
		return c
	}
	none := func(*d.StatPart_Glow) {}
	runStateCases(t, []stateCase{
		{name: "a lit pawn", row: row(none), ctx: pawn(func(*PawnState) {}), want: 7.5},
		{name: "a definition request", row: row(none), want: 10},
		{name: "unspawned", row: row(none), ctx: func() *StatContext { c := pawn(func(*PawnState) {}); c.Spawned = Some(false); return c }(), want: 10},
		{name: "a shambler", row: row(none), ctx: pawn(func(p *PawnState) { p.Shambler = Some(true) }), want: 10},
		{name: "blind, ignored", row: row(func(g *d.StatPart_Glow) { g.IgnoreIfIncapableOfSight = true }), ctx: pawn(func(p *PawnState) { p.Blind = Some(true) }), want: 10},
		{name: "blind, not ignored", row: row(none), ctx: pawn(func(p *PawnState) { p.Blind = Some(true) }), want: 7.5},
		{name: "ideo prefers darkness", row: row(func(g *d.StatPart_Glow) { g.IgnoreIfPrefersDarkness = true }), ctx: pawn(func(p *PawnState) { p.IdeoPrefersDarkness = Some(true) }), want: 10},
		{name: "genes unaffected by darkness", row: row(func(g *d.StatPart_Glow) { g.IgnoreIfPrefersDarkness = true }), ctx: pawn(func(p *PawnState) { p.GenesAffectedByDarkness = Some(false) }), want: 10},
		{name: "humanlikeOnly animal", row: row(func(g *d.StatPart_Glow) { g.HumanlikeOnly = true }), animal: true, ctx: pawn(func(*PawnState) {}), want: 10},
		{name: "pawnOnly non-pawn", row: row(func(g *d.StatPart_Glow) { g.PawnOnly = true }), ctx: &StatContext{Spawned: Some(true), GlowLevel: Some(float32(1))}, want: 10},
		{name: "non-pawn lit", row: row(none), ctx: &StatContext{Spawned: Some(true), GlowLevel: Some(float32(1))}, want: 10},
		{name: "glow not observed", row: row(none), ctx: &StatContext{Spawned: Some(true)}, err: "glow"},
		{name: "spawned not observed", row: row(none), ctx: &StatContext{}, err: "spawned"},
		{name: "prefers darkness unobserved only matters when ignored", row: row(none), ctx: func() *StatContext {
			c := pawn(func(p *PawnState) { p.IdeoPrefersDarkness = Known[bool]{} })
			return c
		}(), want: 7.5},
		{name: "prefers darkness unobserved when ignored", row: row(func(g *d.StatPart_Glow) { g.IgnoreIfPrefersDarkness = true }), ctx: pawn(func(p *PawnState) { p.IdeoPrefersDarkness = Known[bool]{} }), err: "darkness"},
	})
}

func TestStatePartsPlaceAndRoom(t *testing.T) {
	out := &d.StatPart_Outdoors{FactorIndoors: 0.5, FactorOutdoors: 2}
	room := func(edit func(*RoomState)) Known[*RoomState] {
		r := &RoomState{
			OutdoorsForWork: Some(false), PsychologicallyOutdoors: Some(false), Role: Some("Bedroom"),
			Stats: map[string]float32{"Cleanliness": 2},
		}
		edit(r)
		return Some(r)
	}
	plainRoom := func(*RoomState) {}
	ctx := func(r Known[*RoomState], edit func(*StatContext)) *StatContext {
		c := &StatContext{Room: r, Spawned: Some(true), Roofed: Some(true), Temperature: Some(float32(20))}
		edit(c)
		return c
	}
	same := func(*StatContext) {}
	stat := &d.StatPart_RoomStat{RoomStat: "Cleanliness"}
	runStateCases(t, []stateCase{
		{name: "outdoors: definition request is indoors", row: out, want: 5},
		{name: "outdoors: no room", row: out, ctx: ctx(Some[*RoomState](nil), same), want: 5},
		{name: "outdoors: roofed indoors", row: out, ctx: ctx(room(plainRoom), same), want: 5},
		{name: "outdoors: OutdoorsForWork", row: out, ctx: ctx(room(func(r *RoomState) { r.OutdoorsForWork = Some(true) }), same), want: 20},
		{name: "outdoors: unroofed", row: out, ctx: ctx(room(plainRoom), func(c *StatContext) { c.Roofed = Some(false) }), want: 20},
		{name: "outdoors: unspawned is not unroofed", row: out, ctx: ctx(room(plainRoom), func(c *StatContext) { c.Spawned, c.Roofed = Some(false), Some(false) }), want: 5},
		{name: "outdoors: roof not observed", row: out, ctx: ctx(room(plainRoom), func(c *StatContext) { c.Roofed = Known[bool]{} }), err: "roofed"},
		{name: "outdoors: room not observed", row: out, ctx: &StatContext{}, err: "room"},
		{name: "room stat", row: stat, ctx: ctx(room(plainRoom), same), want: 20},
		{name: "room stat: no room", row: stat, ctx: ctx(Some[*RoomState](nil), same), want: 10},
		{name: "room stat: definition request", row: stat, want: 10},
		{name: "room stat: unknown stat", row: &d.StatPart_RoomStat{RoomStat: "Beauty"}, ctx: ctx(room(plainRoom), same), err: "Beauty"},
		{name: "table outdoors", row: &d.StatPart_WorkTableOutdoors{}, ctx: ctx(room(func(r *RoomState) { r.PsychologicallyOutdoors = Some(true) }), same), want: 8},
		{name: "table indoors", row: &d.StatPart_WorkTableOutdoors{}, ctx: ctx(room(plainRoom), same), want: 10},
		{name: "table unspawned", row: &d.StatPart_WorkTableOutdoors{}, ctx: ctx(room(func(r *RoomState) { r.PsychologicallyOutdoors = Some(true) }), func(c *StatContext) { c.Spawned = Some(false) }), want: 10},
		{name: "table outdoors: definition request", row: &d.StatPart_WorkTableOutdoors{}, want: 10},
		{name: "table cold", row: &d.StatPart_WorkTableTemperature{}, ctx: ctx(room(plainRoom), func(c *StatContext) { c.Temperature = Some(float32(8.9)) }), want: 7},
		{name: "table hot", row: &d.StatPart_WorkTableTemperature{}, ctx: ctx(room(plainRoom), func(c *StatContext) { c.Temperature = Some(float32(35.1)) }), want: 7},
		{name: "table comfortable", row: &d.StatPart_WorkTableTemperature{}, ctx: ctx(room(plainRoom), same), want: 10},
		{name: "table edge 9", row: &d.StatPart_WorkTableTemperature{}, ctx: ctx(room(plainRoom), func(c *StatContext) { c.Temperature = Some(float32(9)) }), want: 10},
		{name: "table unspawned temperature", row: &d.StatPart_WorkTableTemperature{}, ctx: &StatContext{Spawned: Some(false)}, want: 10},
		{name: "table temperature not observed", row: &d.StatPart_WorkTableTemperature{}, ctx: &StatContext{Spawned: Some(true)}, err: "temperature"},
		{name: "table in another role", row: &d.StatPart_WorkTableRoomRole{}, ctx: ctx(room(plainRoom), same), want: 5},
		{name: "table in its role", row: &d.StatPart_WorkTableRoomRole{}, ctx: ctx(room(func(r *RoomState) { r.Role = Some("Workshop") }), same), want: 10},
		{name: "table role outdoors", row: &d.StatPart_WorkTableRoomRole{}, ctx: ctx(room(func(r *RoomState) { r.PsychologicallyOutdoors = Some(true) }), same), want: 10},
		{name: "table role no room", row: &d.StatPart_WorkTableRoomRole{}, ctx: ctx(Some[*RoomState](nil), same), want: 10},
		{name: "table role not observed", row: &d.StatPart_WorkTableRoomRole{}, ctx: ctx(room(func(r *RoomState) { r.Role = Known[string]{} }), same), err: "role"},
	})
}

func TestStatePartsNeedsAndHealth(t *testing.T) {
	rest := &d.StatPart_Rest{FactorExhausted: 0.1, FactorVeryTired: 0.2, FactorTired: 0.5, FactorRested: 2}
	food := &d.StatPart_Food{FactorStarving: 0.25, FactorUrgentlyHungry: 0.5, FactorHungry: 0.75, FactorFed: 2}
	withRest := func(c string) *StatContext {
		return pawnCtx(func(p *PawnState) { p.Rest = Some(NeedState{Present: true, Category: c}) })
	}
	withFood := func(c string) *StatContext {
		return pawnCtx(func(p *PawnState) { p.Food = Some(NeedState{Present: true, Category: c}) })
	}
	mood := &d.StatPart_Mood{FactorFromMoodCurve: curve(0, 0, 1, 2)}
	withMood := func(m MoodState) *StatContext { return pawnCtx(func(p *PawnState) { p.Mood = Some(m) }) }
	pain := &d.StatPart_Pain{Factor: -2}
	withPain := func(v float32) *StatContext { return pawnCtx(func(p *PawnState) { p.PainTotal = Some(v) }) }
	runStateCases(t, []stateCase{
		{name: "exhausted", row: rest, ctx: withRest(RestExhausted), want: 1},
		{name: "very tired", row: rest, ctx: withRest(RestVeryTired), want: 2},
		{name: "tired", row: rest, ctx: withRest(RestTired), want: 5},
		{name: "rested", row: rest, ctx: withRest(RestRested), want: 20},
		{name: "no rest need", row: rest, ctx: pawnCtx(func(p *PawnState) { p.Rest = Some(NeedState{}) }), want: 10},
		{name: "rest: definition request", row: rest, want: 10},
		{name: "rest not observed", row: rest, ctx: pawnCtx(func(*PawnState) {}), err: "rest"},
		{name: "rest category unknown", row: rest, ctx: withRest("Dozing"), err: "Dozing"},
		{name: "starving", row: food, ctx: withFood(FoodStarving), want: 2.5},
		{name: "urgently hungry", row: food, ctx: withFood(FoodUrgentlyHungry), want: 5},
		{name: "hungry", row: food, ctx: withFood(FoodHungry), want: 7.5},
		{name: "fed", row: food, ctx: withFood(FoodFed), want: 20},
		{name: "mood", row: mood, ctx: withMood(MoodState{Present: true, Level: 0.25}), want: 5},
		{name: "no mood need", row: mood, ctx: withMood(MoodState{}), want: 10},
		{name: "mood not observed", row: mood, ctx: pawnCtx(func(*PawnState) {}), err: "mood"},
		{name: "pain", row: pain, ctx: withPain(0.25), want: 5},
		{name: "no pain", row: pain, ctx: withPain(0), want: 10},
		{name: "pain: non-pawn", row: pain, ctx: &StatContext{}, want: 10},
		{name: "pain not observed", row: pain, ctx: pawnCtx(func(*PawnState) {}), err: "pain"},
		{name: "malnutrition", row: &d.StatPart_Malnutrition{Curve: curve(0, 1, 1, 0)}, ctx: pawnCtx(func(p *PawnState) { p.Malnutrition = Some(MalnutritionState{Present: true, Severity: 0.5}) }), want: 5},
		{name: "no malnutrition", row: &d.StatPart_Malnutrition{Curve: curve(0, 1, 1, 0)}, ctx: pawnCtx(func(p *PawnState) { p.Malnutrition = Some(MalnutritionState{}) }), want: 10},
		{name: "malnutrition not observed", row: &d.StatPart_Malnutrition{Curve: curve(0, 1, 1, 0)}, ctx: pawnCtx(func(*PawnState) {}), err: "Malnutrition"},
	})
}

func TestStatePartsPawnFlags(t *testing.T) {
	flag := func(edit func(*PawnState, bool)) func(bool) *StatContext {
		return func(v bool) *StatContext { return pawnCtx(func(p *PawnState) { edit(p, v) }) }
	}
	slave := flag(func(p *PawnState, v bool) { p.IsSlave = Some(v) })
	dead := flag(func(p *PawnState, v bool) { p.Deathresting = Some(v) })
	wild := flag(func(p *PawnState, v bool) { p.IsWildMan = Some(v) })
	resting := func(r RestingState) *StatContext { return pawnCtx(func(p *PawnState) { p.Resting = Some(r) }) }
	rowResting := &d.StatPart_Resting{Factor: 3}
	runStateCases(t, []stateCase{
		{name: "slave", row: &d.StatPart_Slave{Factor: 0.75}, ctx: slave(true), want: 7.5},
		{name: "free", row: &d.StatPart_Slave{Factor: 0.75}, ctx: slave(false), want: 10},
		{name: "slave not observed", row: &d.StatPart_Slave{Factor: 0.75}, ctx: pawnCtx(func(*PawnState) {}), err: "slave"},
		{name: "slave: definition request", row: &d.StatPart_Slave{Factor: 0.75}, want: 10},
		{name: "deathresting", row: &d.StatPart_Deathresting{Factor: 0.5}, ctx: dead(true), want: 5},
		{name: "awake", row: &d.StatPart_Deathresting{Factor: 0.5}, ctx: dead(false), want: 10},
		{name: "wild man", row: &d.StatPart_WildManOffset{Offset: 4}, ctx: wild(true), want: 14},
		{name: "tame", row: &d.StatPart_WildManOffset{Offset: 4}, ctx: wild(false), want: 10},
		{name: "in bed", row: rowResting, ctx: resting(RestingState{InBed: true}), want: 30},
		{name: "lying down", row: rowResting, ctx: resting(RestingState{NotStanding: true}), want: 30},
		{name: "downed on the ground is not resting", row: rowResting, ctx: resting(RestingState{NotStanding: true, Downed: true}), want: 10},
		{name: "standing", row: rowResting, ctx: resting(RestingState{}), want: 10},
		{name: "halted caravan member", row: rowResting, ctx: resting(RestingState{CaravanMember: true}), want: 30},
		{name: "moving caravan member", row: rowResting, ctx: resting(RestingState{CaravanMember: true, CaravanMoving: true}), want: 10},
		{name: "in a caravan bed", row: rowResting, ctx: resting(RestingState{InCaravanBed: true}), want: 30},
		{name: "carried by a caravan", row: rowResting, ctx: resting(RestingState{CarriedByCaravan: true}), want: 30},
		{name: "resting not observed", row: rowResting, ctx: pawnCtx(func(*PawnState) {}), err: "resting"},
	})
}

func TestStatePartGenes(t *testing.T) {
	set := func(archites int32, factors ...float32) *StatContext {
		return &StatContext{Genepack: Some(&GeneSetState{ArchitesTotal: archites, MarketValueFactors: factors})}
	}
	row := &d.StatPart_Genes{}
	runStateCases(t, []stateCase{
		{name: "a genepack", row: row, biotech: true, ctx: set(1, 2, 1, 1, 1), want: 120}, // 10 * 1.5 * 4 * 2
		{name: "the gene count factor floors at 0.5", row: row, biotech: true, ctx: set(0, 1, 1, 1, 1, 1, 1, 1, 1), want: 5},
		{name: "not a genepack", row: row, biotech: true, ctx: &StatContext{Genepack: Some[*GeneSetState](nil)}, want: 10},
		{name: "without Biotech", row: row, ctx: set(1, 2), want: 10},
		{name: "definition request", row: row, biotech: true, want: 10},
		{name: "gene set not observed", row: row, biotech: true, ctx: &StatContext{}, err: "gene set"},
	})
}

func TestStatePartsRowShapeMismatch(t *testing.T) {
	if _, err := rowOf[*d.StatPart_Age](&d.StatPart_Glow{}, "StatPart_Age"); err == nil {
		t.Error("a mismatched row was accepted")
	}
}

// A thing request is refused by the public entry points until the base
// worker's thing-side terms are ported; a terrain takes no context.
func TestThingRequestsAreNotMirroredYet(t *testing.T) {
	r := newRig(t, func(stat *d.StatDef, parka, _ *d.ThingDef) {
		parka.StatBases = append(parka.StatBases, mod(testStat, 1))
	})
	subject := ThingSubject("Apparel_Parka", "")
	subject.Context = &StatContext{}
	_, err := r.eval.Value(testStat, subject)
	notMirrored(t, err, "StatWorker")
	_, err = r.eval.ShouldShowFor(testStat, subject)
	notMirrored(t, err, "StatWorker")
	_, err = r.eval.Evaluate(testStat, subject)
	notMirrored(t, err, "StatWorker")
	terrain := TerrainSubject("Soil")
	terrain.Context = &StatContext{}
	if _, err := r.eval.request(testStat, terrain); err == nil {
		t.Error("a terrain with a thing context was accepted")
	}
}
