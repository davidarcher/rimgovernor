package snapshot

import (
	"context"
	"fmt"
	"os"
	"path/filepath"
	"sync"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Planner is one shelter planner step as recorded (#745): the pure
// decisions it made and the inputs each took, the native site reads
// among them. The routine review's facts (Routine) say which goals open;
// this says where the shelter is sited and how an excavation proceeds,
// which the planner decides from its own colony read at step time.
type Planner struct {
	Recorded string
	Snapshot domain.GenerationSnapshot
	Tick     domain.Tick
	Goal     policy.GoalID
	// Shelter is every starter search the step ran, in order.
	Shelter []policy.StarterRequest
	// Excavation is the geometric dig search the step ran, if any.
	Excavation []policy.ExcavationSiteRequest
	// Sites is every native excavation site read, in order.
	Sites []ExcavationRead
	// Choices is every dig-or-shell choice the step made.
	Choices []ExcavationChoice
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

// ExcavationChoice is one policy.ChooseExcavation call and its answer.
type ExcavationChoice struct {
	Anchor   domain.Cell
	Shell    *policy.StarterLayout
	Target   *policy.ExcavationTarget
	Excavate bool
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
func StartPlanner(ctx context.Context, goal policy.GoalID) (context.Context, func(domain.GenerationSnapshot, domain.Tick) error) {
	dir := os.Getenv(DirEnv)
	if dir == "" {
		return ctx, func(domain.GenerationSnapshot, domain.Tick) error { return nil }
	}
	rec := &plannerRecorder{p: Planner{Goal: goal}}
	return context.WithValue(ctx, plannerKey{}, rec), func(current domain.GenerationSnapshot, tick domain.Tick) error {
		rec.mu.Lock()
		defer rec.mu.Unlock()
		p := rec.p
		if len(p.Shelter)+len(p.Excavation)+len(p.Sites)+len(p.Choices) == 0 {
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

// NoteShelter records a starter search's request.
func NoteShelter(ctx context.Context, r policy.StarterRequest) {
	recorder(ctx).add(func(p *Planner) { p.Shelter = append(p.Shelter, r) })
}

// NoteExcavation records a geometric dig search's request.
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

// NoteChoice records a dig-or-shell choice.
func NoteChoice(ctx context.Context, c ExcavationChoice) {
	recorder(ctx).add(func(p *Planner) { p.Choices = append(p.Choices, c) })
}

// RecordPlanner writes p into dir as planner-<goal>-<tick>.json.
func RecordPlanner(dir string, p Planner) error {
	data, err := Encode(p)
	if err != nil {
		return err
	}
	if err = os.MkdirAll(dir, 0o755); err != nil {
		return err
	}
	return os.WriteFile(filepath.Join(dir, fmt.Sprintf("planner-%s-%d.json", p.Goal, p.Tick)), data, 0o644)
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
