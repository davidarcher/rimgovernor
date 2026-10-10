package stateval

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/bridge/recordedrows"
	"github.com/davidarcher/RimGovernor/go/internal/testkit/recordedcatalog"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

const (
	defAnimal = "Muffalo"
	defHuman  = "Human"
)

// pawnStatEval is an evaluator over the recorded stat, flesh-type and mutant
// rows, the Muffalo and Human defs, and two synthetic mutants.
func pawnStatEval(t *testing.T, odyssey bool) *Evaluator {
	t.Helper()
	slice := recordedrows.Take(t, recordedrows.Named(defAnimal, defHuman), "stat_defs", "flesh_type_defs", "mutant_defs", "life_stage_defs")
	slice.Wire.Defs.MutantDefs = append(slice.Wire.Defs.MutantDefs,
		&d.MutantDef{DefName: "TestNoAir", BreathesAir: false}, &d.MutantDef{DefName: "TestAir", BreathesAir: true})
	catalog, err := recordedcatalog.FromSlice(slice, "unit")
	if err != nil {
		t.Fatal(err)
	}
	mods := map[string]bool{"ludeon.rimworld": true}
	if odyssey {
		mods[modOdyssey] = true
	}
	return New(catalog, Env{ActiveMods: mods, ScenarioFactors: map[string]float32{}})
}

func (e *Evaluator) req(t *testing.T, stat, def string, pawn *PawnState) *Request {
	t.Helper()
	subject := ThingSubject(def, "")
	if pawn != nil {
		subject.Context = &StatContext{Pawn: pawn}
	}
	req, err := e.request(stat, subject)
	if err != nil {
		t.Fatal(err)
	}
	return req
}

// withBase states the facts the base worker reads (with nothing there) on a pawn
// a test builds, so a request that reaches StatWorker.GetValueUnfinalized for the
// pawn evaluates.
func withBase(p *PawnState) *PawnState {
	p.Base.Skills = Some(SkillsState{Present: true, Levels: map[string]int32{}})
	p.Base.Capacities = Some(map[string]float32{})
	p.Base.Story = Some(StoryState{Present: true})
	p.Base.Apparel = Some(WornGear{Present: true})
	p.Base.Primary = Some[*GearPiece](nil)
	p.Base.Inspiration = Some("")
	p.Body.Hediffs = Some([]HediffState{})
	p.Body.Ideo = Some(IdeoState{})
	p.Body.Genes = Some(GenesState{})
	p.Body.CurLifeStage = Some("HumanlikeAdult")
	return p
}

func baseIs(shown bool) func() (bool, error) {
	return func() (bool, error) { return shown, nil }
}

func baseMustNotRun(t *testing.T) func() (bool, error) {
	return func() (bool, error) {
		t.Helper()
		t.Error("base ShouldShowFor ran")
		return false, nil
	}
}

type showCase struct {
	name string
	def  string
	pawn *PawnState
	base bool
	want bool
	err  string
}

func runShow(t *testing.T, e *Evaluator, stat string, w showWorker, cases []showCase) {
	t.Helper()
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			got, err := w.Show(e.req(t, stat, c.def, c.pawn), baseIs(c.base))
			if c.err != "" {
				wantErr(t, err, c.err)
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

func TestPawnStatWorkersAreOwned(t *testing.T) {
	owned := ownedWorkers()
	for _, class := range []string{
		"StatWorker_Mechanitor", "StatWorker_Terror", "StatWorker_Wildness", "StatWorker_SuppressionFallRate",
		"StatWorker_VacuumResistance", "StatWorker_MinimumHandlingSkill", "StatWorker_ForagedNutritionPerDay",
		"StatWorker_MeatAmount", "StatWorker_LeatherAmount", "StatWorker_CaravanRidingSpeedFactor",
	} {
		if owned[class] == nil {
			t.Errorf("%s is not owned", class)
		}
	}
	for _, class := range []string{"StatWorker_LeatherAmount", "StatWorker_CaravanRidingSpeedFactor"} {
		w := owned[class]
		if _, ok := w.(showWorker); ok {
			t.Errorf("%s overrides ShouldShowFor", class)
		}
		if _, ok := w.(unfinalizedWorker); ok {
			t.Errorf("%s overrides GetValueUnfinalized", class)
		}
		if _, ok := w.(baseValueWorker); ok {
			t.Errorf("%s overrides GetBaseValueFor", class)
		}
	}
}

func TestMechanitorShow(t *testing.T) {
	e := pawnStatEval(t, false)
	yes := &PawnState{PawnStat: PawnStatFacts{IsMechanitor: Some(true)}}
	no := &PawnState{PawnStat: PawnStatFacts{IsMechanitor: Some(false)}}
	runShow(t, e, "MechBandwidth", workerMechanitor{}, []showCase{
		{name: "definition request", def: defHuman, base: true, want: false},
		{name: "mechanitor", def: defHuman, pawn: yes, base: true, want: true},
		{name: "not a mechanitor", def: defHuman, pawn: no, base: true, want: false},
		{name: "base hides", def: defHuman, pawn: yes, base: false, want: false},
		{name: "missing fact", def: defHuman, pawn: &PawnState{}, base: true, err: "MechanitorUtility.IsMechanitor"},
	})
}

func TestTerrorShowAndValue(t *testing.T) {
	e := pawnStatEval(t, false)
	slave := &PawnState{IsSlave: Some(true)}
	runShow(t, e, "Terror", workerTerror{}, []showCase{
		{name: "definition request", def: defHuman, base: true, want: false},
		{name: "slave", def: defHuman, pawn: slave, base: true, want: true},
		{name: "free", def: defHuman, pawn: &PawnState{IsSlave: Some(false)}, base: true, want: false},
		{name: "base hides", def: defHuman, pawn: slave, base: false, want: false},
		{name: "missing slave", def: defHuman, pawn: &PawnState{}, base: true, err: "Pawn.IsSlave"},
	})
	value := func(p *PawnState) (float32, error) {
		return workerTerror{}.Unfinalized(e.req(t, "Terror", defHuman, p))
	}
	with := func(slave bool, n ...int32) *PawnState {
		return &PawnState{IsSlave: Some(slave), PawnStat: PawnStatFacts{TerrorIntensities: Some(n)}}
	}
	for _, c := range []struct {
		name string
		pawn *PawnState
		want float32
	}{
		{"non-slave ignores thoughts", with(false, 90), 0},
		{"no thoughts", with(true), 0},
		{"sum below the cap", with(true, 20, 5), 0.25},
		{"capped at 100", with(true, 70, 60, 10), 1},
		{"free pawn needs no thoughts", &PawnState{IsSlave: Some(false)}, 0},
	} {
		t.Run(c.name, func(t *testing.T) {
			got, err := value(c.pawn)
			if err != nil {
				t.Fatal(err)
			}
			if got != c.want {
				t.Errorf("= %v, want %v", got, c.want)
			}
		})
	}
	_, err := value(&PawnState{IsSlave: Some(true)})
	wantErr(t, err, "terror thought intensities")
	_, err = value(&PawnState{})
	wantErr(t, err, "Pawn.IsSlave")
	_, err = workerTerror{}.Unfinalized(e.req(t, "Terror", defHuman, nil))
	wantErr(t, err, "needs a pawn")
}

func TestWildnessShow(t *testing.T) {
	e := pawnStatEval(t, false)
	pawn := func(wild, animal Known[bool]) *PawnState {
		return &PawnState{IsWildMan: wild, PawnStat: PawnStatFacts{IsAnimal: animal}}
	}
	runShow(t, e, "Wildness", workerWildness{}, []showCase{
		{name: "animal definition", def: defAnimal, base: false, want: true},
		{name: "humanlike definition", def: defHuman, base: true, want: false},
		{name: "wild man ignores the animal fact", def: defHuman, pawn: pawn(Some(true), Known[bool]{}), base: false, want: true},
		{name: "animal pawn", def: defAnimal, pawn: pawn(Some(false), Some(true)), base: false, want: true},
		{name: "colonist", def: defHuman, pawn: pawn(Some(false), Some(false)), base: true, want: false},
		{name: "subhuman animal falls back on the def", def: defAnimal, pawn: pawn(Some(false), Some(false)), base: false, want: true},
		{name: "missing wild man", def: defHuman, pawn: pawn(Known[bool]{}, Some(true)), err: "Pawn.IsWildMan"},
		{name: "missing animal", def: defHuman, pawn: pawn(Some(false), Known[bool]{}), err: "Pawn.IsAnimal"},
	})
	// The base is never consulted.
	if _, err := (workerWildness{}).Show(e.req(t, "Wildness", defAnimal, nil), baseMustNotRun(t)); err != nil {
		t.Fatal(err)
	}
	// A terrain is not a ThingDef.
	req, err := e.request("Wildness", TerrainSubject("Soil"))
	if err == nil {
		if got, err := (workerWildness{}).Show(req, baseIs(true)); err != nil || got {
			t.Errorf("terrain = %v, %v, want false", got, err)
		}
	}
}

func TestSuppressionFallRate(t *testing.T) {
	e := pawnStatEval(t, false)
	slave := &PawnState{IsSlave: Some(true)}
	runShow(t, e, "SlaveSuppressionFallRate", workerSuppressionFallRate{}, []showCase{
		{name: "definition request", def: defHuman, base: true, want: false},
		{name: "slave", def: defHuman, pawn: slave, base: true, want: true},
		{name: "free", def: defHuman, pawn: &PawnState{IsSlave: Some(false)}, base: true, want: false},
		{name: "base hides", def: defHuman, pawn: slave, base: false, want: false},
		{name: "missing slave", def: defHuman, pawn: &PawnState{}, base: true, err: "Pawn.IsSlave"},
	})
	game, err := e.catalog.GameConstants()
	if err != nil {
		t.Fatal(err)
	}
	c := game.GetStatWorker_SuppressionFallRate()
	at := func(p float32) *PawnState {
		return &PawnState{PawnStat: PawnStatFacts{Suppression: Some(SuppressionNeedState{Present: true, CurLevelPercentage: p})}}
	}
	for _, tc := range []struct {
		name string
		p    float32
		want float32
	}{
		{"above the fast threshold", c.GetFastFallRateThreshold() + 0.01, c.GetFastFallRate()},
		{"at the fast threshold", c.GetFastFallRateThreshold(), c.GetMediumFallRate()},
		{"above the medium threshold", c.GetMediumFallRateThreshold() + 0.01, c.GetMediumFallRate()},
		{"at the medium threshold", c.GetMediumFallRateThreshold(), c.GetSlowFallRate()},
		{"empty", 0, c.GetSlowFallRate()},
	} {
		t.Run(tc.name, func(t *testing.T) {
			got, err := workerSuppressionFallRate{}.Unfinalized(e.req(t, "SlaveSuppressionFallRate", defHuman, at(tc.p)))
			if err != nil {
				t.Fatal(err)
			}
			if got != tc.want {
				t.Errorf("= %v, want %v", got, tc.want)
			}
		})
	}
	if c.GetFastFallRate() != 0.2 || c.GetMediumFallRate() != 0.1 || c.GetSlowFallRate() != 0.05 {
		t.Errorf("fall rates %v %v %v differ from the game's 0.2 0.1 0.05", c.GetFastFallRate(), c.GetMediumFallRate(), c.GetSlowFallRate())
	}
	_, err = workerSuppressionFallRate{}.Unfinalized(e.req(t, "SlaveSuppressionFallRate", defHuman, &PawnState{}))
	wantErr(t, err, "Need_Suppression")
	none := &PawnState{PawnStat: PawnStatFacts{Suppression: Some(SuppressionNeedState{})}}
	_, err = workerSuppressionFallRate{}.Unfinalized(e.req(t, "SlaveSuppressionFallRate", defHuman, none))
	wantErr(t, err, "the pawn has none")
	_, err = workerSuppressionFallRate{}.Unfinalized(e.req(t, "SlaveSuppressionFallRate", defHuman, nil))
	wantErr(t, err, "needs a pawn")
}

func TestVacuumResistance(t *testing.T) {
	e := pawnStatEval(t, false)
	runShow(t, e, "VacuumResistance", workerVacuumResistance{}, []showCase{
		{name: "definition request", def: defHuman, base: true, want: false},
		{name: "flesh pawn", def: defHuman, pawn: &PawnState{}, base: true, want: true},
		{name: "base hides", def: defHuman, pawn: &PawnState{}, base: false, want: false},
	})
	// A mechanoid race is not flesh.
	flesh := e.catalog.ThingDef(defHuman).GetRace().GetFleshType()
	if flesh == "" {
		flesh = fleshNormal
	}
	mutant := func(name string) *PawnState {
		return withBase(&PawnState{PawnStat: PawnStatFacts{MutantDef: Some(name)}})
	}
	base, err := e.baseUnfinalized(e.req(t, "VacuumResistance", defHuman, nil))
	if err != nil {
		t.Fatal(err)
	}
	for _, c := range []struct {
		name  string
		pawn  *PawnState
		want  float32
		wantE string
	}{
		{"definition request is the base", nil, base, ""},
		{"not a mutant is the base", mutant(""), base, ""},
		{"mutant that breathes air is the base", mutant("TestAir"), base, ""},
		{"mutant that does not breathe air is immune", mutant("TestNoAir"), 1, ""},
		{"unknown mutant", mutant("Nope"), 0, "no mutant def Nope"},
		{"missing fact", &PawnState{}, 0, "mutant def"},
	} {
		t.Run(c.name, func(t *testing.T) {
			got, err := workerVacuumResistance{}.Unfinalized(e.req(t, "VacuumResistance", defHuman, c.pawn))
			if c.wantE != "" {
				wantErr(t, err, c.wantE)
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

func TestMinimumHandlingSkill(t *testing.T) {
	e := pawnStatEval(t, false)
	w := workerMinimumHandlingSkill{}
	game, err := e.catalog.GameConstants()
	if err != nil {
		t.Fatal(err)
	}
	curve := game.GetStatWorker_MinimumHandlingSkill().GetHandlingSkillFromWildness()
	wild, err := e.Value("Wildness", ThingSubject(defAnimal, ""))
	if err != nil {
		t.Fatal(err)
	}
	skill, err := EvaluateCurve(curve, wild)
	if err != nil {
		t.Fatal(err)
	}
	want := clamp32(skill, 0, 20)
	got, err := w.Unfinalized(e.req(t, "MinimumHandlingSkill", defAnimal, nil))
	if err != nil {
		t.Fatal(err)
	}
	if got != want {
		t.Errorf("animal definition = %v, want %v (Wildness %v)", got, want, wild)
	}
	if got, err = w.Unfinalized(e.req(t, "MinimumHandlingSkill", defHuman, nil)); err != nil || got != 0 {
		t.Errorf("humanlike definition = %v, %v, want 0", got, err)
	}
	// A thing request reads the thing's own Wildness.
	thing := e.req(t, "MinimumHandlingSkill", defAnimal, withBase(&PawnState{}))
	wildThing, err := e.value(e.req(t, "Wildness", defAnimal, withBase(&PawnState{})))
	if err != nil {
		t.Fatal(err)
	}
	skill, err = EvaluateCurve(curve, wildThing)
	if err != nil {
		t.Fatal(err)
	}
	if got, err = w.Unfinalized(thing); err != nil || got != clamp32(skill, 0, 20) {
		t.Errorf("animal thing = %v, %v, want %v", got, err, clamp32(skill, 0, 20))
	}
	if got, err = w.Unfinalized(e.req(t, "MinimumHandlingSkill", defHuman, &PawnState{})); err != nil || got != 0 {
		t.Errorf("humanlike thing = %v, %v, want 0", got, err)
	}
	terrain, err := e.request("MinimumHandlingSkill", TerrainSubject("Soil"))
	if err == nil {
		_, err = w.Unfinalized(terrain)
		wantErr(t, err, "needs a ThingDef")
	}
}

func TestForagedNutritionPerDay(t *testing.T) {
	w := workerForagedNutritionPerDay{}
	learned := &PawnState{PawnStat: PawnStatFacts{IsAnimal: Some(true), Training: Some(TrainingState{Present: true, ForageLearned: true})}, Body: BodyFacts{CurLifeStage: Some("HumanlikeAdult")}}
	untrained := &PawnState{PawnStat: PawnStatFacts{IsAnimal: Some(true), Training: Some(TrainingState{Present: true})}, Body: BodyFacts{CurLifeStage: Some("HumanlikeAdult")}}
	noTracker := &PawnState{PawnStat: PawnStatFacts{IsAnimal: Some(true), Training: Some(TrainingState{})}}
	person := &PawnState{PawnStat: PawnStatFacts{IsAnimal: Some(false), Training: Some(TrainingState{Present: true, ForageLearned: true})}}

	t.Run("show", func(t *testing.T) {
		on, off := pawnStatEval(t, true), pawnStatEval(t, false)
		runShow(t, on, "ForagedNutritionPerDay", w, []showCase{
			{name: "definition request is the base", def: defAnimal, base: true, want: true},
			{name: "definition request base hides", def: defAnimal, base: false, want: false},
			{name: "animal that learned forage", def: defAnimal, pawn: learned, base: false, want: true},
			{name: "animal that did not", def: defAnimal, pawn: untrained, base: true, want: false},
			{name: "animal with no tracker is the base", def: defAnimal, pawn: noTracker, base: true, want: true},
			{name: "non-animal is the base", def: defHuman, pawn: person, base: false, want: false},
			{name: "missing animal", def: defAnimal, pawn: &PawnState{}, base: true, err: "Pawn.IsAnimal"},
			{name: "missing training", def: defAnimal, pawn: &PawnState{PawnStat: PawnStatFacts{IsAnimal: Some(true)}}, base: true, err: "training tracker"},
		})
		runShow(t, off, "ForagedNutritionPerDay", w, []showCase{
			{name: "without Odyssey a trained animal is hidden", def: defAnimal, pawn: learned, base: true, want: false},
			{name: "without Odyssey the tracker-less animal is the base", def: defAnimal, pawn: noTracker, base: true, want: true},
		})
	})

	t.Run("base value", func(t *testing.T) {
		on, off := pawnStatEval(t, true), pawnStatEval(t, false)
		plain, err := w.BaseValue(on.req(t, "ForagedNutritionPerDay", defAnimal, nil))
		if err != nil {
			t.Fatal(err)
		}
		if want := baseValue(on.req(t, "ForagedNutritionPerDay", defAnimal, nil)); plain != want {
			t.Errorf("definition = %v, want %v", plain, want)
		}
		game, err := on.catalog.GameConstants()
		if err != nil {
			t.Fatal(err)
		}
		factor := game.GetStatWorker_ForagedNutritionPerDay().GetForgeBodySizeFactor()
		bodySize := mul(bridge.DefRow[*d.LifeStageDef](on.catalog, "HumanlikeAdult").GetBodySizeFactor(), on.catalog.ThingDef(defAnimal).GetRace().GetBaseBodySize())
		if factor != 0.6 {
			t.Errorf("ForgeBodySizeFactor = %v, want 0.6", factor)
		}
		for _, c := range []struct {
			name string
			e    *Evaluator
			pawn *PawnState
			want float32
			err  string
		}{
			{"trained animal", on, learned, float32(bodySize * factor), ""},
			{"untrained animal", on, untrained, plain, ""},
			{"no tracker", on, noTracker, plain, ""},
			{"without Odyssey", off, learned, plain, ""},
			{"missing training", on, &PawnState{}, 0, "training tracker"},
			{"missing life stage", on, &PawnState{PawnStat: PawnStatFacts{Training: Some(TrainingState{Present: true, ForageLearned: true})}}, 0, "life stage"},
		} {
			t.Run(c.name, func(t *testing.T) {
				got, err := w.BaseValue(c.e.req(t, "ForagedNutritionPerDay", defAnimal, c.pawn))
				if c.err != "" {
					wantErr(t, err, c.err)
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
	})
}

func TestMeatAmountShow(t *testing.T) {
	e := pawnStatEval(t, false)
	race := e.catalog.ThingDef(defAnimal).GetRace()
	if !race.GetHasMeat() {
		t.Fatalf("%s has no meat", defAnimal)
	}
	runShow(t, e, "MeatAmount", workerMeatAmount{}, []showCase{
		{name: "definition request is the base", def: defAnimal, base: true, want: true},
		{name: "definition request base hides", def: defAnimal, base: false, want: false},
		{name: "pawn with meat is hidden", def: defAnimal, pawn: &PawnState{}, base: true, want: false},
	})
	race.HasMeat = false
	defer func() { race.HasMeat = true }()
	runShow(t, e, "MeatAmount", workerMeatAmount{}, []showCase{
		{name: "pawn without meat is the base", def: defAnimal, pawn: &PawnState{}, base: true, want: true},
		{name: "pawn without meat, base hides", def: defAnimal, pawn: &PawnState{}, base: false, want: false},
	})
}
