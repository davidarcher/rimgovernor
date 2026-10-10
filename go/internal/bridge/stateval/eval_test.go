package stateval

import (
	"errors"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/bridge/recordedrows"
	"github.com/davidarcher/RimGovernor/go/internal/testkit/recordedcatalog"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	"google.golang.org/protobuf/proto"
)

const testStat = "TestStat"

func mod(stat string, v float32) *d.Opt_StatModifier {
	return &d.Opt_StatModifier{Value: &d.StatModifier{Stat: stat, Value: v}}
}

func curve(points ...float32) *d.SimpleCurve {
	c := &d.SimpleCurve{}
	for i := 0; i < len(points); i += 2 {
		c.Points = append(c.Points, &d.CurvePoint{Loc: &d.Vector2{X: points[i], Y: points[i+1]}})
	}
	return c
}

func partAge(priority float32) *d.Opt_StatPartAny {
	return &d.Opt_StatPartAny{Value: &d.StatPartAny{Value: &d.StatPartAny_StatPart_Age{StatPart_Age: &d.StatPart_Age{Priority: priority}}}}
}

func partStuff(priority float32) *d.Opt_StatPartAny {
	return &d.Opt_StatPartAny{Value: &d.StatPartAny{Value: &d.StatPartAny_StatPart_Stuff{StatPart_Stuff: &d.StatPart_Stuff{Priority: priority}}}}
}

// rig is a small catalog (Apparel_Parka, Steel and a free terrain) with a
// synthetic stat the test shapes through edit.
type rig struct {
	t    *testing.T
	stat *d.StatDef
	eval *Evaluator
}

func newRig(t *testing.T, edit func(stat *d.StatDef, parka, steel *d.ThingDef)) *rig {
	t.Helper()
	slice := recordedrows.Take(t, recordedrows.Named("Apparel_Parka", "Steel"), "stat_defs", "stat_category_defs", "flesh_type_defs")
	stat := &d.StatDef{
		DefName: testStat, Category: "BasicsNonPawn", WorkerClass: "RimWorld.StatWorker",
		ShowOnUntradeables: true, ShowOnUnhaulables: true, ShowIfUndefined: true,
		ApplyFactorsIfNegative: true, MinValue: -1e6, MaxValue: 1e6, RoundToFiveOver: 3.4028235e38,
	}
	var parka, steel *d.ThingDef
	for _, row := range slice.Wire.ThingDefs {
		switch row.DefName {
		case "Apparel_Parka":
			parka = row
		case "Steel":
			steel = row
		}
	}
	edit(stat, parka, steel)
	slice.Wire.Defs.StatDefs = append(slice.Wire.Defs.StatDefs, stat)
	catalog, err := recordedcatalog.FromSlice(slice, "unit")
	if err != nil {
		t.Fatal(err)
	}
	env := Env{ActiveMods: map[string]bool{"ludeon.rimworld": true}, ScenarioFactors: map[string]float32{}}
	return &rig{t: t, stat: stat, eval: New(catalog, env)}
}

func (r *rig) value(subject Subject) float32 {
	r.t.Helper()
	v, err := r.eval.Value(testStat, subject)
	if err != nil {
		r.t.Fatal(err)
	}
	return v
}

func notMirrored(t *testing.T, err error, class string) {
	t.Helper()
	var nm *bridge.NotMirrored
	if !errors.As(err, &nm) || nm.Class != class {
		t.Fatalf("err = %v, want NotMirrored for %s", err, class)
	}
}

func TestBaseValueStuffAndFinalize(t *testing.T) {
	r := newRig(t, func(stat *d.StatDef, parka, steel *d.ThingDef) {
		stat.DefaultBaseValue = 3
		parka.StatBases = append(parka.StatBases, mod(testStat, 10), mod(testStat, 99)) // the first entry wins
		steel.StuffProps.StatFactors = append(steel.StuffProps.StatFactors, mod(testStat, 2))
		steel.StuffProps.StatOffsets = append(steel.StuffProps.StatOffsets, mod(testStat, 1))
	})
	parka := ThingSubject("Apparel_Parka", "")
	if got := r.value(parka); got != 10 {
		t.Errorf("statBases = %v, want 10", got)
	}
	if got := r.value(ThingSubject("Apparel_Parka", "Steel")); got != 21 {
		t.Errorf("stuff factor then offset = %v, want 10*2+1", got)
	}
	if got := r.value(ThingSubject("Steel", "")); got != 3 {
		t.Errorf("default base = %v, want 3", got)
	}
}

func TestNegativeValuesSkipStuffFactorUnlessApplied(t *testing.T) {
	for _, applies := range []bool{true, false} {
		r := newRig(t, func(stat *d.StatDef, parka, steel *d.ThingDef) {
			stat.ApplyFactorsIfNegative = applies
			parka.StatBases = append(parka.StatBases, mod(testStat, -5))
			steel.StuffProps.StatFactors = append(steel.StuffProps.StatFactors, mod(testStat, 2))
			steel.StuffProps.StatOffsets = append(steel.StuffProps.StatOffsets, mod(testStat, 1))
		})
		want := float32(-4) // the offset always applies
		if applies {
			want = -9
		}
		if got := r.value(ThingSubject("Apparel_Parka", "Steel")); got != want {
			t.Errorf("applyFactorsIfNegative=%v: %v, want %v", applies, got, want)
		}
	}
}

func TestFinalizeCurveScenarioRoundClamp(t *testing.T) {
	for _, c := range []struct {
		name string
		edit func(*d.StatDef)
		env  map[string]float32
		base float32
		want float32
	}{
		{"post-process curve", func(s *d.StatDef) { s.PostProcessCurve = curve(0, 0, 10, 100) }, nil, 5, 50},
		{"scenario factor", func(*d.StatDef) {}, map[string]float32{testStat: 3, "Other": 9}, 5, 15},
		{"round half to even", func(s *d.StatDef) { s.RoundValue = true }, nil, 2.5, 2},
		{"round half to even up", func(s *d.StatDef) { s.RoundValue = true }, nil, 3.5, 4},
		{"round to five over", func(s *d.StatDef) { s.RoundToFiveOver = 20 }, nil, 22, 20},
		{"round to five under the line", func(s *d.StatDef) { s.RoundToFiveOver = 20 }, nil, 18, 18},
		{"round to five negative", func(s *d.StatDef) { s.RoundToFiveOver = 20 }, nil, -27, -25},
		{"clamp high", func(s *d.StatDef) { s.MaxValue = 7 }, nil, 12, 7},
		{"clamp low", func(s *d.StatDef) { s.MinValue = -2 }, nil, -12, -2},
	} {
		t.Run(c.name, func(t *testing.T) {
			r := newRig(t, func(stat *d.StatDef, parka, _ *d.ThingDef) {
				c.edit(stat)
				parka.StatBases = append(parka.StatBases, mod(testStat, c.base))
			})
			if c.env != nil {
				r.eval.env.ScenarioFactors = c.env
			}
			if got := r.value(ThingSubject("Apparel_Parka", "")); got != c.want {
				t.Errorf("= %v, want %v", got, c.want)
			}
		})
	}
}

// fakePart records what the dispatch asks of it.
type fakePart struct {
	class string
	add   float32
	force bool
	log   *[]string
}

func (p fakePart) Class() string { return p.class }
func (p fakePart) Transform(_ *Request, val float32) (float32, error) {
	*p.log = append(*p.log, p.class)
	return val*2 + p.add, nil
}
func (p fakePart) ForceShow(*Request) (bool, error) { return p.force, nil }

func TestPartsDispatchByPriorityAndUnownedPartsAreNotMirrored(t *testing.T) {
	r := newRig(t, func(stat *d.StatDef, parka, _ *d.ThingDef) {
		stat.Parts = []*d.Opt_StatPartAny{partAge(1), partStuff(5), partAge(1)}
		stat.Parts[2].Value.GetStatPart_Age().Curve = curve(0, 0) // distinct row, same class
		parka.StatBases = append(parka.StatBases, mod(testStat, 1))
	})
	parka := ThingSubject("Apparel_Parka", "")
	_, err := r.eval.Value(testStat, parka)
	notMirrored(t, err, "StatPart_Stuff") // the highest priority part runs first
	r.eval.parts = map[string]Part{}
	var log []string
	r.eval.parts["StatPart_Stuff"] = fakePart{class: "StatPart_Stuff", add: 1, log: &log}
	_, err = r.eval.Value(testStat, parka)
	notMirrored(t, err, "StatPart_Age")
	r.eval.parts["StatPart_Age"] = fakePart{class: "StatPart_Age", add: 0, log: &log}
	log = nil
	// 1 -> Stuff: 1*2+1 = 3 -> Age: 6 -> Age: 12
	if got := r.value(parka); got != 12 {
		t.Errorf("parts chain = %v, want 12", got)
	}
	if strings.Join(log, ",") != "StatPart_Stuff,StatPart_Age,StatPart_Age" {
		t.Errorf("part order = %v", log)
	}
}

func TestEqualPriorityPartsKeepFileOrder(t *testing.T) {
	r := newRig(t, func(stat *d.StatDef, parka, _ *d.ThingDef) {
		stat.Parts = []*d.Opt_StatPartAny{partAge(0), partStuff(0)}
		parka.StatBases = append(parka.StatBases, mod(testStat, 1))
	})
	var log []string
	r.eval.parts = map[string]Part{
		"StatPart_Age":   fakePart{class: "StatPart_Age", log: &log},
		"StatPart_Stuff": fakePart{class: "StatPart_Stuff", log: &log},
	}
	r.value(ThingSubject("Apparel_Parka", ""))
	if strings.Join(log, ",") != "StatPart_Age,StatPart_Stuff" {
		t.Errorf("order = %v", log)
	}
}

func TestWorkerSubclassIsNotMirrored(t *testing.T) {
	r := newRig(t, func(stat *d.StatDef, _, _ *d.ThingDef) { stat.WorkerClass = "RimWorld.StatWorker_Terror" })
	_, err := r.eval.Value(testStat, ThingSubject("Apparel_Parka", ""))
	notMirrored(t, err, "StatWorker_Terror")
	_, err = r.eval.ShouldShowFor(testStat, ThingSubject("Apparel_Parka", ""))
	notMirrored(t, err, "StatWorker_Terror")
}

func TestShouldShowFor(t *testing.T) {
	parka := ThingSubject("Apparel_Parka", "")
	shown := func(t *testing.T, r *rig) bool {
		t.Helper()
		got, err := r.eval.ShouldShowFor(testStat, parka)
		if err != nil {
			t.Fatal(err)
		}
		return got
	}
	t.Run("always hide", func(t *testing.T) {
		if shown(t, newRig(t, func(s *d.StatDef, _, _ *d.ThingDef) { s.AlwaysHide = true })) {
			t.Error("shown")
		}
	})
	t.Run("undefined", func(t *testing.T) {
		r := newRig(t, func(s *d.StatDef, _, _ *d.ThingDef) { s.ShowIfUndefined = false })
		if shown(t, r) {
			t.Error("shown without a statBases entry")
		}
		r = newRig(t, func(s *d.StatDef, p, _ *d.ThingDef) {
			s.ShowIfUndefined = false
			p.StatBases = append(p.StatBases, mod(testStat, 1))
		})
		if !shown(t, r) {
			t.Error("hidden with a statBases entry")
		}
	})
	t.Run("a force-show part wins before the category", func(t *testing.T) {
		r := newRig(t, func(s *d.StatDef, _, _ *d.ThingDef) {
			s.Category = "Weapon_Melee"
			s.Parts = []*d.Opt_StatPartAny{partAge(0)}
		})
		var log []string
		r.eval.parts = map[string]Part{"StatPart_Age": fakePart{class: "StatPart_Age", force: true, log: &log}}
		if !shown(t, r) {
			t.Error("hidden despite ForceShow")
		}
		r.eval.parts["StatPart_Age"] = fakePart{class: "StatPart_Age", log: &log}
		if shown(t, r) {
			t.Error("a parka is not a melee weapon")
		}
	})
	t.Run("an unowned part is reached only after the cheap checks", func(t *testing.T) {
		r := newRig(t, func(s *d.StatDef, _, _ *d.ThingDef) { s.Parts = []*d.Opt_StatPartAny{partAge(0)} })
		_, err := r.eval.ShouldShowFor(testStat, parka)
		notMirrored(t, err, "StatPart_Age")
		r = newRig(t, func(s *d.StatDef, _, _ *d.ThingDef) {
			s.AlwaysHide = true
			s.Parts = []*d.Opt_StatPartAny{partAge(0)}
		})
		if shown(t, r) {
			t.Error("alwaysHide shown")
		}
	})
	t.Run("classic mode", func(t *testing.T) {
		r := newRig(t, func(s *d.StatDef, _, _ *d.ThingDef) { s.HideInClassicMode = true })
		if !shown(t, r) {
			t.Error("hidden outside classic mode")
		}
		r.eval.env.ClassicMode = true
		if shown(t, r) {
			t.Error("shown in classic mode")
		}
	})
	t.Run("mods", func(t *testing.T) {
		r := newRig(t, func(s *d.StatDef, _, _ *d.ThingDef) { s.ShowIfModsLoaded = []string{"ludeon.rimworld.royalty"} })
		if shown(t, r) {
			t.Error("shown without the mod")
		}
		r.eval.env.ActiveMods["ludeon.rimworld.royalty"] = true
		if !shown(t, r) {
			t.Error("hidden with the mod")
		}
		any := newRig(t, func(s *d.StatDef, _, _ *d.ThingDef) {
			s.ShowIfModsLoadedAny = []string{"ludeon.rimworld.royalty", "ludeon.rimworld.biotech"}
		})
		if shown(t, any) {
			t.Error("shown with neither mod")
		}
		any.eval.env.ActiveMods["ludeon.rimworld.biotech"] = true
		if !shown(t, any) {
			t.Error("hidden with one of the mods")
		}
		any.eval.env.ActiveMods = nil
		if _, err := any.eval.ShouldShowFor(testStat, parka); err == nil || !strings.Contains(err.Error(), "active mod set") {
			t.Errorf("no mod set: %v", err)
		}
	})
	t.Run("category", func(t *testing.T) {
		for cat, want := range map[string]bool{"Apparel": true, "Weapon": false, "Terrain": false, "BasicsPawn": false, "Building": false, "BasicsNonPawn": true} {
			r := newRig(t, func(s *d.StatDef, _, _ *d.ThingDef) { s.Category = cat })
			if got := shown(t, r); got != want {
				t.Errorf("%s: shown = %v, want %v", cat, got, want)
			}
		}
		r := newRig(t, func(s *d.StatDef, _, _ *d.ThingDef) { s.Category = "Terrain" })
		if got, err := r.eval.ShouldShowFor(testStat, TerrainSubject(r.eval.catalog.TerrainDefs[anyKey(r.eval.catalog.TerrainDefs)].DefName)); err != nil || !got {
			t.Errorf("terrain stat on terrain = %v, %v", got, err)
		}
	})
	t.Run("untradeable stats read MarketValue, which is not ported", func(t *testing.T) {
		r := newRig(t, func(s *d.StatDef, _, _ *d.ThingDef) { s.ShowOnUntradeables = false })
		_, err := r.eval.ShouldShowFor(testStat, parka)
		notMirrored(t, err, "StatWorker_MarketValue")
	})
}

func anyKey[V any](m map[string]V) string {
	for k := range m {
		return k
	}
	return ""
}

func TestRequestErrors(t *testing.T) {
	r := newRig(t, func(*d.StatDef, *d.ThingDef, *d.ThingDef) {})
	terrain := anyKey(r.eval.catalog.TerrainDefs)
	bad := int32(7)
	for name, c := range map[string]struct {
		stat    string
		subject Subject
		want    string
	}{
		"unknown stat":    {"NoSuchStat", ThingSubject("Apparel_Parka", ""), "no stat def"},
		"unknown thing":   {testStat, ThingSubject("NoSuchThing", ""), "no thing def"},
		"unknown stuff":   {testStat, ThingSubject("Apparel_Parka", "NoSuchStuff"), "no stuff def"},
		"unknown terrain": {testStat, TerrainSubject("NoSuchTerrain"), "no terrain def"},
		"terrain stuff":   {testStat, Subject{Def: terrain, Terrain: true, Stuff: "Steel"}, "takes no stuff"},
		"bad quality":     {testStat, Subject{Def: "Apparel_Parka", Quality: &bad}, "not a quality category"},
	} {
		t.Run(name, func(t *testing.T) {
			if _, err := r.eval.Evaluate(c.stat, c.subject); err == nil || !strings.Contains(err.Error(), c.want) {
				t.Errorf("err = %v, want %q", err, c.want)
			}
		})
	}
	var none *Evaluator
	if _, err := none.Value(testStat, ThingSubject("Apparel_Parka", "")); err == nil {
		t.Error("nil evaluator answered")
	}
}

func TestEmptyPartIsAnError(t *testing.T) {
	r := newRig(t, func(s *d.StatDef, _, _ *d.ThingDef) { s.Parts = []*d.Opt_StatPartAny{{}} })
	if _, err := r.eval.Value(testStat, ThingSubject("Apparel_Parka", "")); err == nil || !strings.Contains(err.Error(), "empty part") {
		t.Errorf("err = %v", err)
	}
}

func TestEvaluateCurve(t *testing.T) {
	c := curve(0, 10, 10, 20, 30, 0)
	for x, want := range map[float32]float32{-5: 10, 0: 10, 5: 15, 10: 20, 20: 10, 30: 0, 99: 0} {
		if got, err := EvaluateCurve(c, x); err != nil || got != want {
			t.Errorf("Evaluate(%v) = %v, %v; want %v", x, got, err, want)
		}
	}
	if got, err := EvaluateCurve(curve(4, 7), 100); err != nil || got != 7 {
		t.Errorf("one point = %v, %v", got, err)
	}
	if _, err := EvaluateCurve(&d.SimpleCurve{}, 1); err == nil {
		t.Error("an empty curve answered")
	}
}

func TestNoSharedMutation(t *testing.T) {
	// The evaluator reads the catalog's rows and never writes them.
	r := newRig(t, func(s *d.StatDef, p, _ *d.ThingDef) {
		s.Parts = []*d.Opt_StatPartAny{partAge(2), partStuff(1)}
		p.StatBases = append(p.StatBases, mod(testStat, 1))
	})
	before := proto.Clone(r.stat)
	r.eval.parts = map[string]Part{"StatPart_Age": fakePart{class: "StatPart_Age", log: new([]string)}, "StatPart_Stuff": fakePart{class: "StatPart_Stuff", log: new([]string)}}
	r.value(ThingSubject("Apparel_Parka", ""))
	if !proto.Equal(before, r.stat) {
		t.Error("evaluation changed the stat row")
	}
}
