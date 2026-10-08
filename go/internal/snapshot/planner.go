package snapshot

import (
	"context"
	"errors"
	"fmt"
	"io/fs"
	"os"
	"path/filepath"
	"sync"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Planner is one planner step as recorded (#745): the pure decisions it
// made and the inputs each took, the native site reads among them. The
// rounds's facts (Routine) say which goals open; this says how an excavation
// proceeds, which the planner decides from its own colony read at step time.
type Planner struct {
	Recorded string
	Snapshot domain.GenerationSnapshot
	Tick     domain.Tick
	Concern  policy.ConcernID
	// Excavation is the tunnel corridor search the step ran, if any.
	Excavation []policy.ExcavationSiteRequest
	// Sites is every native excavation site read, in order.
	Sites []ExcavationRead
	// ShrineSquads is every shrine defender read, in order.
	ShrineSquads [][]policy.ShrineDefenderFacts
	// ShrineReadiness is every policy.ShrineBreachReadiness request.
	ShrineReadiness []policy.ShrineReadinessRequest
	// ResourceMethods is every policy.SelectResourceMethod request: the
	// fresh bench census and ingredient stock a production bill is chosen
	// from (#894).
	ResourceMethods []policy.ResourceMethodRequest
	// Workshops is every policy.SelectWorkshopBench request: the recipe
	// catalog and bench census a workshop bench is chosen from.
	Workshops []policy.WorkshopRequest
	// GearMethods is every policy.SelectGearMethod request.
	GearMethods []policy.GearPlanningRequest
	// Research is every research selection's fresh census.
	Research []ResearchCall
}

// ResearchCall is one research selection's inputs: the staged policy
// (armor rungs applied), the recorded research needs and the native
// research read without its observation context.
type ResearchCall struct {
	Policy policy.RoundsPolicy
	Needs  []string
	Read   bridge.ResearchRead
}

// ExcavationRead is one native excavation site read: what was asked
// (Purpose names the planner's question) and the reply without its
// observation context.
type ExcavationRead struct {
	Purpose string
	Target  policy.ExcavationTarget
	Cells   []domain.Cell
	Site    bridge.ExcavationSite
}

type plannerKey struct{}

// plannerRecorder collects one step's Planner; nil when not recording.
type plannerRecorder struct {
	mu sync.Mutex
	p  Planner
}

// StartPlanner returns ctx carrying a recorder for one planner step when
// DirEnv is set; finish writes what the step recorded (nothing when it
// recorded nothing) and reports a failed write.
func StartPlanner(ctx context.Context, goal policy.ConcernID) (context.Context, func(domain.GenerationSnapshot, domain.Tick) error) {
	dir := os.Getenv(DirEnv)
	if dir == "" {
		return ctx, func(domain.GenerationSnapshot, domain.Tick) error { return nil }
	}
	rec := &plannerRecorder{p: Planner{Concern: goal}}
	return context.WithValue(ctx, plannerKey{}, rec), func(current domain.GenerationSnapshot, tick domain.Tick) error {
		rec.mu.Lock()
		defer rec.mu.Unlock()
		p := rec.p
		if len(p.Excavation)+len(p.Sites)+len(p.ShrineSquads)+len(p.ShrineReadiness)+
			len(p.ResourceMethods)+len(p.Workshops)+len(p.GearMethods)+len(p.Research) == 0 {
			return nil
		}
		p.Snapshot, p.Tick = current, tick
		p.Recorded = fmt.Sprintf("colony %s load %s map %d tick %d", current.Colony, current.Load, current.Map, tick)
		return RecordPlanner(dir, p)
	}
}

func recorder(ctx context.Context) *plannerRecorder {
	rec, _ := ctx.Value(plannerKey{}).(*plannerRecorder)
	return rec
}

func (rec *plannerRecorder) add(f func(*Planner)) {
	if rec == nil {
		return
	}
	rec.mu.Lock()
	defer rec.mu.Unlock()
	f(&rec.p)
}

// NoteExcavation records a tunnel corridor search's request.
func NoteExcavation(ctx context.Context, r policy.ExcavationSiteRequest) {
	recorder(ctx).add(func(p *Planner) { p.Excavation = append(p.Excavation, r) })
}

// NoteSite records a native excavation site read.
func NoteSite(ctx context.Context, purpose string, target policy.ExcavationTarget, cells []domain.Cell, site bridge.ExcavationSite) {
	site.Context = nil
	recorder(ctx).add(func(p *Planner) {
		p.Sites = append(p.Sites, ExcavationRead{Purpose: purpose, Target: target, Cells: append([]domain.Cell(nil), cells...), Site: site})
	})
}

// NoteShrineSquad records a shrine defender read.
func NoteShrineSquad(ctx context.Context, squad []policy.ShrineDefenderFacts) {
	recorder(ctx).add(func(p *Planner) { p.ShrineSquads = append(p.ShrineSquads, squad) })
}

// NoteShrineReadiness records a shrine breach readiness request.
func NoteShrineReadiness(ctx context.Context, r policy.ShrineReadinessRequest) {
	recorder(ctx).add(func(p *Planner) { p.ShrineReadiness = append(p.ShrineReadiness, r) })
}

// NoteResourceMethod records a production bill selection's request.
func NoteResourceMethod(ctx context.Context, r policy.ResourceMethodRequest) {
	recorder(ctx).add(func(p *Planner) { p.ResourceMethods = append(p.ResourceMethods, r) })
}

// NoteWorkshop records a workshop bench selection's request.
func NoteWorkshop(ctx context.Context, r policy.WorkshopRequest) {
	recorder(ctx).add(func(p *Planner) { p.Workshops = append(p.Workshops, r) })
}

// NoteGearMethod records a gear method selection's request.
func NoteGearMethod(ctx context.Context, r policy.GearPlanningRequest) {
	recorder(ctx).add(func(p *Planner) { p.GearMethods = append(p.GearMethods, r) })
}

// NoteResearch records a research selection's inputs.
func NoteResearch(ctx context.Context, c ResearchCall) {
	c.Read.Context = nil
	recorder(ctx).add(func(p *Planner) { p.Research = append(p.Research, c) })
}

// RecordPlanner writes p into dir as planner-<goal>-<tick>-<seq>.json,
// seq the first from 1 not already taken.
func RecordPlanner(dir string, p Planner) error {
	data, err := Encode(p)
	if err != nil {
		return err
	}
	if err = os.MkdirAll(dir, 0o755); err != nil {
		return err
	}
	// Several steps at one paused tick each keep their own file.
	for seq := 1; ; seq++ {
		f, err := os.OpenFile(filepath.Join(dir, fmt.Sprintf("planner-%s-%d-%d.json", p.Concern, p.Tick, seq)), os.O_WRONLY|os.O_CREATE|os.O_EXCL, 0o644)
		if errors.Is(err, fs.ErrExist) {
			continue
		}
		if err != nil {
			return err
		}
		_, err = f.Write(data)
		return errors.Join(err, f.Close())
	}
}

// LoadPlanner reads a recorded planner step.
func LoadPlanner(path string) (Planner, error) {
	data, err := readFile(path)
	if err != nil {
		return Planner{}, err
	}
	var p Planner
	if err = Decode(data, &p); err != nil {
		return Planner{}, fmt.Errorf("%s: %w", path, err)
	}
	return p, nil
}
