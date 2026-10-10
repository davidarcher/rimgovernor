// Package stateval is the Go port of RimWorld's stat evaluation over the
// mirrored def rows (epic #2621): StatWorker.GetValue for a definition request
// (a ThingDef or TerrainDef with optional stuff and quality, the request
// bridge.Client.EvaluateStat answers too) and ShouldShowFor. It is
// pure: it reads the decoded catalog and calls nothing native.
//
// The core is the base StatWorker: the stat's base value (statBases, else the
// StatDef default), the stuff factor and offset, the StatPart list in
// priority order, the post-process curve and factors, the scenario factor,
// rounding and the min/max clamp. A StatPart or StatWorker subclass that no Go
// function owns yet is a *NotMirrored naming the class, never a
// default; cmd/stataudit's stat_classes.tsv records which classes are owned.
// A thing request (Subject.Context) is answered from its StatContext, whose
// typed facts hold the live state the rows lack; a fact it does not state is
// an error naming it.
//
// Values are float32 throughout and every operation is rounded to float32, as
// the game's, so a value is bit-identical to the game's when every class it
// touches is owned.
package stateval

import (
	"fmt"
	"math"
	"slices"
	"sort"
	"strings"

	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	"google.golang.org/protobuf/proto"
)

// baseWorker is the class of a StatDef with no workerClass: the core itself.
const baseWorker = "StatWorker"

// Env is the game state a definition request reads that the rows do not hold.
type Env struct {
	// ActiveMods are the active mods' package ids (ModsConfig.IsActive), lower
	// case. Nil means the caller did not supply them: a stat that depends on
	// the mod set is then an error, never "no mods".
	ActiveMods map[string]bool
	// ClassicMode is the ideology manager's classic mode.
	ClassicMode bool
	// ScenarioFactors are the scenario's stat factors (Scenario.GetStatFactor)
	// by stat; a stat absent from the map has factor 1. The caller states
	// them: an empty map is a scenario with none.
	ScenarioFactors map[string]float32
	// Difficulty is the storyteller's difficulty (Find.Storyteller). Unstated,
	// a stat that reads it is an error naming it.
	Difficulty Known[Difficulty]
}

// Subject is a definition request: a ThingDef, or a TerrainDef when Terrain is
// set, with optional stuff and quality category (QualityCategory, 0 awful to
// 6 legendary; nil is normal).
type Subject struct {
	Def     string
	Terrain bool
	Stuff   string
	Quality *int32
	// Context is the live state of a thing request, which the evaluator
	// answers from it alone (see StatContext); nil is a definition request,
	// which has no thing. A thing's quality is the context's, so Quality must
	// be nil with one.
	Context *StatContext
}

// ThingSubject is the request for a ThingDef made of stuff (empty for none).
func ThingSubject(def, stuff string) Subject { return Subject{Def: def, Stuff: stuff} }

// TerrainSubject is the request for a TerrainDef.
func TerrainSubject(def string) Subject { return Subject{Def: def, Terrain: true} }

// Request is a StatRequest for a definition, resolved against the catalog.
type Request struct {
	Evaluator *Evaluator
	Stat      *d.StatDef
	Subject   Subject
	// Thing is the ThingDef, nil for a terrain; Terrain the TerrainDef, nil for
	// a thing.
	Thing   *d.ThingDef
	Terrain *d.TerrainDef
	// Stuff is the stuff's ThingDef, nil for none.
	Stuff *d.ThingDef
	// Quality is the request's QualityCategory (normal when the subject has none).
	Quality int32
}

// statBases is the requested def's statBases (StatRequest.StatBases).
func (r *Request) statBases() []*d.Opt_StatModifier {
	if r.Thing != nil {
		return r.Thing.GetStatBases()
	}
	return r.Terrain.GetStatBases()
}

// Part is one ported StatPart class.
type Part interface {
	// Class is the StatPart class name, as in stat_classes.tsv.
	Class() string
	// Transform is StatPart.TransformValue; row is the part's own entry of
	// the stat's parts list (a *defspb.<Class>), which carries its fields.
	Transform(req *Request, row proto.Message, val float32) (float32, error)
	// ForceShow is StatPart.ForceShow.
	ForceShow(req *Request, row proto.Message) (bool, error)
}

// ownedParts are the StatPart classes a Go function owns, by class. Each has
// "stateval.<Class>" as its owner in stat_classes.tsv.
func ownedParts() map[string]Part {
	parts := []Part{partHyperlinks{}, partQuality{}, partQualityOffset{}, partStuff{}}
	parts = append(parts, statePartList()...)
	parts = append(parts, pawnPartList()...)
	parts = append(parts, thingPartList()...)
	parts = append(parts, gearPartList()...)
	owned := make(map[string]Part, len(parts))
	for _, p := range parts {
		owned[p.Class()] = p
	}
	return owned
}

// Evaluator evaluates stats over one catalog.
type Evaluator struct {
	catalog Catalog
	env     Env
	parts   map[string]Part
	workers map[string]Worker
}

// New is the evaluator of catalog under env.
func New(catalog Catalog, env Env) *Evaluator {
	return &Evaluator{catalog: catalog, env: env, parts: ownedParts(), workers: ownedWorkers()}
}

func (e *Evaluator) modActive(pkg string) (bool, error) {
	if e.env.ActiveMods == nil {
		return false, fmt.Errorf("stat evaluation needs the active mod set (%s): none supplied", pkg)
	}
	return e.env.ActiveMods[strings.ToLower(pkg)], nil
}

// Result is one stat evaluation: the final value and whether the game shows
// the stat for the subject.
type Result struct {
	Value float32
	Shown bool
}

// Evaluate is the game's GetStatValueAbstract and ShouldShowFor of stat for
// subject. A class no Go function owns is a *NotMirrored.
func (e *Evaluator) Evaluate(stat string, subject Subject) (Result, error) {
	req, err := e.request(stat, subject)
	if err != nil {
		return Result{}, err
	}
	value, err := e.value(req)
	if err != nil {
		return Result{}, err
	}
	shown, err := e.shown(req)
	if err != nil {
		return Result{}, err
	}
	return Result{Value: value, Shown: shown}, nil
}

// Value is StatWorker.GetValue (the final value) of stat for subject.
func (e *Evaluator) Value(stat string, subject Subject) (float32, error) {
	req, err := e.request(stat, subject)
	if err != nil {
		return 0, err
	}
	return e.value(req)
}

// ShouldShowFor is StatWorker.ShouldShowFor of stat for subject.
func (e *Evaluator) ShouldShowFor(stat string, subject Subject) (bool, error) {
	req, err := e.request(stat, subject)
	if err != nil {
		return false, err
	}
	return e.shown(req)
}

// ValueWithoutPostProcess is StatWorker.GetValue(req, applyPostProcess:
// false): the post-process curve, the post-process stat factors and the
// min/max clamp are skipped.
func (e *Evaluator) ValueWithoutPostProcess(stat string, subject Subject) (float32, error) {
	req, err := e.request(stat, subject)
	if err != nil {
		return 0, err
	}
	return e.valueWith(req, false)
}

func (e *Evaluator) request(stat string, subject Subject) (*Request, error) {
	if e == nil || e.catalog == nil {
		return nil, fmt.Errorf("stat evaluation has no catalog")
	}
	def := DefRow[*d.StatDef](e.catalog, stat)
	if def == nil {
		return nil, fmt.Errorf("catalog has no stat def %s", stat)
	}
	req := &Request{Evaluator: e, Stat: def, Subject: subject, Quality: 2}
	if subject.Context != nil && subject.Terrain {
		return nil, fmt.Errorf("terrain %s is not a thing: it takes no thing context", subject.Def)
	}
	if subject.Terrain {
		if req.Terrain = e.catalog.TerrainDef(subject.Def); req.Terrain == nil {
			return nil, fmt.Errorf("catalog has no terrain def %s", subject.Def)
		}
	} else if req.Thing = e.catalog.ThingDef(subject.Def); req.Thing == nil {
		return nil, fmt.Errorf("catalog has no thing def %s", subject.Def)
	}
	if subject.Stuff != "" {
		if subject.Terrain {
			return nil, fmt.Errorf("terrain %s takes no stuff", subject.Def)
		}
		if req.Stuff = e.catalog.ThingDef(subject.Stuff); req.Stuff == nil {
			return nil, fmt.Errorf("catalog has no stuff def %s", subject.Stuff)
		}
	}
	if subject.Context != nil {
		// StatRequest.For(thing): the quality is the thing's TryGetQuality,
		// Normal without a CompQuality. Quality parts read it through
		// Request.qualityCategory, which names a quality not observed.
		if subject.Quality != nil {
			return nil, fmt.Errorf("a thing request takes its quality from the context, not Subject.Quality")
		}
		if q := subject.Context.Base.Quality; q.OK && q.V.Has {
			req.Quality = q.V.Category
		}
	}
	if q := subject.Quality; q != nil {
		if *q < 0 || *q > 6 {
			return nil, fmt.Errorf("quality %d is not a quality category", *q)
		}
		req.Quality = *q
	}
	return req, nil
}

// workerClass is the short name of the stat's StatWorker class.
func workerClass(stat *d.StatDef) string {
	class := stat.GetWorkerClass()
	if class == "" {
		return baseWorker
	}
	return class[strings.LastIndex(class, ".")+1:]
}

// orderedParts are the stat's parts as the game orders them (StatDef
// PostLoad: by descending priority, stable), each resolved to its class.
type resolvedPart struct {
	class    string
	priority float32
	row      proto.Message
}

func (e *Evaluator) orderedParts(stat *d.StatDef) ([]resolvedPart, error) {
	parts := make([]resolvedPart, 0, len(stat.GetParts()))
	for i, opt := range stat.GetParts() {
		part := opt.GetValue()
		if part == nil {
			return nil, fmt.Errorf("stat %s has an empty part at index %d", stat.GetDefName(), i)
		}
		msg := part.ProtoReflect()
		oneof := msg.Descriptor().Oneofs().ByName("value")
		field := msg.WhichOneof(oneof)
		if field == nil {
			return nil, fmt.Errorf("stat %s part %d names no class", stat.GetDefName(), i)
		}
		inner := msg.Get(field).Message()
		priority := inner.Get(inner.Descriptor().Fields().ByName("priority")).Float()
		parts = append(parts, resolvedPart{class: string(field.Name()), priority: float32(priority), row: inner.Interface()})
	}
	sort.SliceStable(parts, func(i, j int) bool { return -parts[i].priority < -parts[j].priority })
	return parts, nil
}

// part is the owned port of class, or the NotMirrored error naming it.
func (e *Evaluator) part(class, fact string) (Part, error) {
	if p, ok := e.parts[class]; ok {
		return p, nil
	}
	return nil, &NotMirrored{Class: class, Fact: fact}
}

// value is StatWorker.GetValue: the worker's GetValueUnfinalized, finalized.
func (e *Evaluator) value(req *Request) (float32, error) { return e.valueWith(req, true) }

// valueWith is StatWorker.GetValue(req, applyPostProcess).
func (e *Evaluator) valueWith(req *Request, applyPostProcess bool) (float32, error) {
	if inner, err := e.minifiedInner(req); err != nil {
		return 0, err
	} else if inner != nil {
		return e.valueWith(inner, applyPostProcess)
	}
	w, err := e.worker(req.Stat, "the worker's value computation")
	if err != nil {
		return 0, err
	}
	var val float32
	if vw, ok := w.(unfinalizedWorker); ok {
		val, err = vw.Unfinalized(req)
	} else {
		val, err = e.baseUnfinalized(req)
	}
	if err != nil {
		return 0, err
	}
	return e.finalizeWith(req, val, applyPostProcess)
}

// minifiedInner is the inner thing's request when the stat is
// minifiedThingInherits and the request's thing is a MinifiedThing with an
// inner thing; nil otherwise (a null inner thing logs an error in the game
// and evaluates the minified thing itself).
func (e *Evaluator) minifiedInner(req *Request) (*Request, error) {
	c := req.ctx()
	if c == nil || !req.Stat.GetMinifiedThingInherits() {
		return nil, nil
	}
	minified, err := e.classIsA(req.Thing.GetThingClass(), classMinifiedThing)
	if err != nil || !minified {
		return nil, err
	}
	inner, err := need(c.Base.Minified, "the inner thing of the minified thing")
	if err != nil || inner == nil {
		return nil, err
	}
	return e.request(req.Stat.GetDefName(), *inner)
}

// baseUnfinalized is the base StatWorker.GetValueUnfinalized, which a worker
// override reaches as base.GetValueUnfinalized. A definition request has no
// thing, so it is the base value and the stuff's factor and offset; a thing
// request adds the pawn's offsets and factors, the stuff's quality terms and
// the thing's comps, statFactors, skill and capacity factors (base_thing.go).
func (e *Evaluator) baseUnfinalized(req *Request) (float32, error) {
	stat := req.Stat
	val, err := e.baseValueFor(req)
	if err != nil {
		return 0, err
	}
	pawn := req.pawn()
	if pawn != nil {
		if val, err = e.pawnOffsetTerms(req, pawn, val); err != nil {
			return 0, err
		}
		if val, err = e.pawnFactorTerms(req, pawn, val); err != nil {
			return 0, err
		}
	}
	if req.Stuff != nil {
		props := req.Stuff.GetStuffProps()
		if props == nil {
			return 0, fmt.Errorf("stuff %s has no stuffProps", req.Stuff.GetDefName())
		}
		quality, err := req.qualityState()
		if err != nil {
			return 0, err
		}
		if val > 0 || stat.GetApplyFactorsIfNegative() {
			factor, err := modifierFromList(props.GetStatFactors(), stat.GetDefName(), 1)
			if err != nil {
				return 0, err
			}
			val = float32(val * factor)
			if quality.Has {
				qf, err := qualityListValue(props.GetStatFactorsQuality(), stat.GetDefName(), quality.Category, 1)
				if err != nil {
					return 0, err
				}
				val = float32(val * qf)
			}
		}
		offset, err := modifierFromList(props.GetStatOffsets(), stat.GetDefName(), 0)
		if err != nil {
			return 0, err
		}
		val = float32(val + offset)
		if quality.Has {
			qo, err := qualityListValue(props.GetStatOffsetsQuality(), stat.GetDefName(), quality.Category, 0)
			if err != nil {
				return 0, err
			}
			val = float32(val + qo)
		}
	}
	if req.ctx() != nil {
		return e.thingTerms(req, val)
	}
	return val, nil
}

// baseValueFor is GetBaseValueFor: the worker's override, else the base
// worker's.
func (e *Evaluator) baseValueFor(req *Request) (float32, error) {
	w, err := e.worker(req.Stat, "GetBaseValueFor")
	if err != nil {
		return 0, err
	}
	if bw, ok := w.(baseValueWorker); ok {
		return bw.BaseValue(req)
	}
	return baseValue(req), nil
}

// baseValue is StatWorker.GetBaseValueFor: the first statBases entry for the
// stat, else the StatDef's default.
func baseValue(req *Request) float32 {
	for _, m := range req.statBases() {
		if m.GetValue().GetStat() == req.Stat.GetDefName() {
			return m.GetValue().GetValue()
		}
	}
	return req.Stat.GetDefaultBaseValue()
}

// modifierFromList is StatUtility.GetStatValueFromList: the first entry for
// stat, else the default.
func modifierFromList(list []*d.Opt_StatModifier, stat string, def float32) (float32, error) {
	for _, m := range list {
		mod := m.GetValue()
		if mod == nil {
			return 0, fmt.Errorf("empty stat modifier entry")
		}
		if mod.GetStat() == stat {
			return mod.GetValue(), nil
		}
	}
	return def, nil
}

// finalize is StatWorker.FinalizeValue.
func (e *Evaluator) finalize(req *Request, val float32) (float32, error) {
	return e.finalizeWith(req, val, true)
}

// finalizeWith is FinalizeValue(req, ref val, applyPostProcess): without the
// post-process the curve, the post-process stat factors and the clamp are
// skipped.
func (e *Evaluator) finalizeWith(req *Request, val float32, applyPostProcess bool) (float32, error) {
	stat := req.Stat
	parts, err := e.orderedParts(stat)
	if err != nil {
		return 0, err
	}
	for _, rp := range parts {
		part, err := e.part(rp.class, "TransformValue")
		if err != nil {
			return 0, err
		}
		if val, err = part.Transform(req, rp.row, val); err != nil {
			return 0, err
		}
	}
	if applyPostProcess && stat.PostProcessCurve != nil {
		if val, err = EvaluateCurve(stat.PostProcessCurve, val); err != nil {
			return 0, fmt.Errorf("stat %s post-process curve: %w", stat.GetDefName(), err)
		}
	}
	// postProcessStatFactors multiply by the thing's own stat value: a
	// definition request has no thing, so they do not apply.
	if applyPostProcess && req.ctx() != nil {
		for _, other := range stat.GetPostProcessStatFactors() {
			v, err := req.statValue(other)
			if err != nil {
				return 0, err
			}
			val = float32(val * v)
		}
	}
	if factor, ok := e.env.ScenarioFactors[stat.GetDefName()]; ok {
		val = float32(val * factor)
	}
	if abs32(val) > stat.GetRoundToFiveOver() {
		val = float32(float32(math.RoundToEven(float64(float32(val/5)))) * 5)
	}
	if stat.GetRoundValue() {
		val = float32(math.RoundToEven(float64(val)))
	}
	if !applyPostProcess {
		return val, nil
	}
	return clamp32(val, stat.GetMinValue(), stat.GetMaxValue()), nil
}

func abs32(v float32) float32 { return float32(math.Abs(float64(v))) }

// clamp32 is Mathf.Clamp: a NaN passes through.
func clamp32(v, lo, hi float32) float32 {
	if v < lo {
		return lo
	}
	if v > hi {
		return hi
	}
	return v
}

// EvaluateCurve is SimpleCurve.Evaluate. A curve with no points is an error
// (the game logs and answers 0).
func EvaluateCurve(curve *d.SimpleCurve, x float32) (float32, error) {
	points := curve.GetPoints()
	if len(points) == 0 {
		return 0, fmt.Errorf("evaluating a curve with no points")
	}
	loc := func(i int) (float32, float32) {
		p := points[i].GetLoc()
		return p.GetX(), p.GetY()
	}
	x0, y0 := loc(0)
	if x <= x0 {
		return y0, nil
	}
	last := len(points) - 1
	xl, yl := loc(last)
	if x >= xl {
		return yl, nil
	}
	ax, ay := x0, y0
	bx, by := xl, yl
	for i := range points {
		px, py := loc(i)
		if x <= px {
			bx, by = px, py
			if i > 0 {
				ax, ay = loc(i - 1)
			}
			break
		}
	}
	t := float32(float32(x-ax) / float32(bx-ax))
	return lerp32(ay, by, t), nil
}

// lerp32 is Mathf.Lerp: t clamped to 0..1. The recorded MeatAmount and
// LeatherAmount values (a post-process curve) are the correctly rounded
// a+(b-a)*t, not the product rounded to float32 first, so the game's Lerp is
// evaluated with a single rounding (fused multiply-add).
func lerp32(a, b, t float32) float32 {
	switch {
	case t < 0:
		t = 0
	case t > 1:
		t = 1
	}
	return float32(float64(a) + float64(float32(b-a))*float64(t))
}

// OwnedClasses are the StatPart and StatWorker classes the evaluator ports,
// sorted.
func OwnedClasses() []string {
	var out []string
	for class := range ownedParts() {
		out = append(out, class)
	}
	for class := range ownedWorkers() {
		out = append(out, class)
	}
	slices.Sort(out)
	return out
}
