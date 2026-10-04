package store

import (
	"context"
	"database/sql"
	"encoding/json"
	"errors"
	"fmt"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// DependencyRecord is one typed shortfall edge (#651) a planner observed at
// admission: the dependent Episode and method, the resource its open
// actions cost, each action's cost, and the stock it was measured against.
// The review keeps the record while the Episode stays active and any of
// the actions stays open, for at most policy.DevelopmentStallTicks; the
// ranking recomputes the shortfall from the actions still open and the
// current stock (roundsDependencies).
type DependencyRecord struct {
	Need     domain.ConcernID
	Goal     domain.ConcernID
	Episode  uint64
	Method   domain.MethodID
	Plan     domain.PlanID
	Resource policy.Resource
	Costs    []policy.DependencyCost
	// Available is the usable stock the admission measured.
	Available int64
	Observed  domain.Tick
}

const maxDependencyRecords = 64

func (d DependencyRecord) validate() error {
	if d.Need == "" || d.Goal == "" || d.Method == "" || d.Plan == "" || d.Resource == "" || len(d.Costs) == 0 || len(d.Costs) > 256 || d.Available < 0 || d.Observed < 0 {
		return errors.New("invalid dependency record")
	}
	if _, ok := policy.ResourcePrerequisite(d.Resource); !ok {
		return errors.New("dependency resource has no prerequisite goal")
	}
	for _, c := range d.Costs {
		if c.Action == "" || c.Count <= 0 || c.Count > 1_000_000 {
			return errors.New("invalid dependency cost")
		}
	}
	return nil
}

// ShortfallDependency is the record for a method admitted under stock
// that does not cover its costs in resource, or false when the stock
// covers them (or the resource has no prerequisite goal): a shelter shell
// is admitted without a stock check (#602), and its frames then wait for
// the difference.
func ShortfallDependency(need domain.ConcernID, goal domain.Standard, method domain.MethodID, plan domain.PlanID, previews []policy.Preview, stock policy.StockObservation, resource policy.Resource, tick domain.Tick) (DependencyRecord, bool) {
	if _, ok := policy.ResourcePrerequisite(resource); !ok {
		return DependencyRecord{}, false
	}
	available := int64(-1)
	for _, s := range stock.Values {
		if v, known := s.Available.Value(); s.Resource == resource && known {
			available = max(0, v)
		}
	}
	if available < 0 {
		return DependencyRecord{}, false
	}
	rec := DependencyRecord{Need: need, Goal: goal.ID, Episode: goal.Episode, Method: method, Plan: plan, Resource: resource, Available: available, Observed: tick}
	var total int64
	for _, p := range previews {
		costs, known := p.Costs.Value()
		if !known {
			continue
		}
		for _, c := range costs {
			if c.Resource == resource && c.Count > 0 {
				rec.Costs = append(rec.Costs, policy.DependencyCost{Action: p.Action.ID(), Count: c.Count})
				total += c.Count
			}
		}
	}
	if total <= available || len(rec.Costs) > 256 {
		return DependencyRecord{}, false
	}
	return rec, true
}

// RecordDependency stores rec in the review at revision, replacing any
// record of the same goal, method and resource. A review that has moved past
// revision is ErrConflict.
func (s *Store) RecordDependency(ctx context.Context, revision uint64, rec DependencyRecord) error {
	if err := rec.validate(); err != nil {
		return err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return err
	}
	defer tx.Rollback()
	review, err := loadRounds(ctx, tx)
	if err != nil {
		return err
	}
	if review.Revision != revision {
		return fmt.Errorf("%w: rounds revision %d, dependency under %d", ErrConflict, review.Revision, revision)
	}
	kept := []DependencyRecord{rec}
	for _, d := range review.Dependencies {
		if d.Goal != rec.Goal || d.Method != rec.Method || d.Resource != rec.Resource {
			kept = append(kept, d)
		}
	}
	if len(kept) > maxDependencyRecords {
		kept = kept[:maxDependencyRecords]
	}
	sortDependencies(kept)
	review.Dependencies = kept
	data, err := json.Marshal(review)
	if err != nil {
		return err
	}
	if len(data) > 1024*1024 {
		return ErrCapacity
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO rounds(singleton,payload) VALUES(1,?) ON CONFLICT(singleton) DO UPDATE SET payload=excluded.payload", data); err != nil {
		return err
	}
	return tx.Commit()
}

func sortDependencies(d []DependencyRecord) {
	sort.SliceStable(d, func(i, j int) bool {
		if d[i].Goal != d[j].Goal {
			return d[i].Goal < d[j].Goal
		}
		if d[i].Method != d[j].Method {
			return d[i].Method < d[j].Method
		}
		return d[i].Resource < d[j].Resource
	})
}

// roundsDependencies keeps the records still live this review and turns
// them into ranking edges. A record drops when its goal is no longer the
// need's active Episode, every one of its actions has settled (the
// dependency is satisfied, cancelled or replaced), or its evidence is
// older than a game day. The shortfall is recomputed from the actions
// still open against the current stock, so wood gained since admission
// shrinks the donation; unknown stock keeps the record but donates nothing.
func roundsDependencies(ctx context.Context, tx *sql.Tx, records []DependencyRecord, bindings []RoundsGoal, states []WorkOwner, facts policy.RoundsFacts, tick domain.Tick) ([]DependencyRecord, []policy.DevelopmentDependency, error) {
	active := map[domain.ConcernID]WorkOwner{}
	for i, b := range bindings {
		active[b.Need] = states[i]
	}
	var kept []DependencyRecord
	var edges []policy.DevelopmentDependency
	for _, rec := range records {
		g, ok := active[rec.Need]
		if !ok || domain.ConcernID(g.OwnerID()) != rec.Goal || g.OwnerEpoch() != rec.Episode || !ownerActive(g) || tick < rec.Observed || tick-rec.Observed > policy.DevelopmentStallTicks {
			continue
		}
		prerequisite, ok := policy.ResourcePrerequisite(rec.Resource)
		if !ok {
			continue
		}
		plan, err := load(ctx, tx, rec.Plan)
		if errors.Is(err, sql.ErrNoRows) || errors.Is(err, ErrNotFound) {
			continue
		}
		if err != nil {
			return nil, nil, err
		}
		if plan.Retired {
			continue
		}
		open := map[domain.ActionID]bool{}
		for _, p := range plan.Progress {
			if ProgressOpen(plan, p) {
				open[p.View().Action] = true
			}
		}
		var costs []policy.DependencyCost
		for _, c := range rec.Costs {
			if open[c.Action] {
				costs = append(costs, c)
			}
		}
		if len(costs) == 0 {
			continue
		}
		kept = append(kept, rec)
		edges = append(edges, policy.DevelopmentDependency{Dependent: rec.Need, Goal: rec.Goal, Episode: rec.Episode, Method: rec.Method, Prerequisite: prerequisite, Resource: rec.Resource, Costs: costs, Available: resourceStock(facts, rec.Resource), Observed: rec.Observed})
	}
	return kept, edges, nil
}

// resourceStock is the current usable stock of resource: the wood fact
// for WoodLog, the item census for any other.
func resourceStock(f policy.RoundsFacts, resource policy.Resource) domain.Fact[int64] {
	if resource == "WoodLog" {
		return f.Wood
	}
	rows, known := f.Resources.Value()
	if !known {
		return domain.Unknown[int64]()
	}
	var n int64
	for _, a := range rows {
		if a.Resource == resource {
			n += a.Count
		}
	}
	return domain.Known(n)
}

// priorDependencies is the last review's still-live edges against its goal
// bindings, read before DetectRounds so an open shortfall raises its
// MaintainResource floor (#711, #728).
func priorDependencies(ctx context.Context, tx *sql.Tx, previous Rounds, facts policy.RoundsFacts, tick domain.Tick) ([]policy.DevelopmentDependency, error) {
	if len(previous.Dependencies) == 0 {
		return nil, nil
	}
	var bindings []RoundsGoal
	var states []WorkOwner
	for _, b := range previous.Goals {
		g, err := loadGoal(ctx, tx, b.Goal)
		if errors.Is(err, sql.ErrNoRows) || errors.Is(err, ErrNotFound) {
			continue
		}
		if err != nil {
			return nil, err
		}
		bindings, states = append(bindings, b), append(states, g)
	}
	for _, b := range previous.Projects {
		g, err := loadProject(ctx, tx, b.Project)
		if errors.Is(err, sql.ErrNoRows) || errors.Is(err, ErrNotFound) {
			continue
		}
		if err != nil {
			return nil, err
		}
		bindings, states = append(bindings, RoundsGoal{Need: b.Need, Goal: domain.ConcernID(b.Project)}), append(states, g)
	}
	_, edges, err := roundsDependencies(ctx, tx, previous.Dependencies, bindings, states, facts, tick)
	return edges, err
}
