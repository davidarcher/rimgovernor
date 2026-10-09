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

// DependencyCost is one open action's cost in a DependencyRecord's resource.
type DependencyCost struct {
	Action domain.ActionID
	Count  int64
}

// DependencyRecord is one admitted method's shortfall a planner
// observed at admission: the dependent Episode and method, the resource its
// open actions cost, each action's cost, and the stock it was measured
// against. It is the one persisted source of admitted-method construction
// demand (policy.ConstructionDemand): the review keeps the record while the
// Episode stays active and any of the actions stays open, for at most
// domain.TicksPerDay (roundsDependencies).
type DependencyRecord struct {
	Need     domain.ConcernID
	Concern  domain.ConcernID
	Episode  uint64
	Method   domain.MethodID
	Plan     domain.PlanID
	Resource policy.Resource
	Costs    []DependencyCost
	// Available is the usable stock the admission measured.
	Available int64
	Observed  domain.Tick
}

const maxDependencyRecords = 64

func (d DependencyRecord) validate() error {
	if d.Need == "" || d.Concern == "" || d.Method == "" || d.Plan == "" || d.Resource == "" || len(d.Costs) == 0 || len(d.Costs) > 256 || d.Available < 0 || d.Observed < 0 {
		return errors.New("invalid dependency record")
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
// covers them: a shelter shell is admitted without a stock check, and
// its frames then wait for the difference.
func ShortfallDependency(need domain.ConcernID, goal domain.Standard, method domain.MethodID, plan domain.PlanID, previews []policy.Preview, stock policy.StockObservation, resource policy.Resource, tick domain.Tick) (DependencyRecord, bool) {
	available := int64(-1)
	for _, s := range stock.Values {
		if v, known := s.Available.Value(); s.Resource == resource && known {
			available = max(0, v)
		}
	}
	if available < 0 {
		return DependencyRecord{}, false
	}
	rec := DependencyRecord{Need: need, Concern: goal.ID, Episode: goal.Episode, Method: method, Plan: plan, Resource: resource, Available: available, Observed: tick}
	var total int64
	for _, p := range previews {
		costs, known := p.Costs.Value()
		if !known {
			continue
		}
		for _, c := range costs {
			if c.Resource == resource && c.Count > 0 {
				rec.Costs = append(rec.Costs, DependencyCost{Action: p.Action.ID(), Count: c.Count})
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
		if d.Concern != rec.Concern || d.Method != rec.Method || d.Resource != rec.Resource {
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
		if d[i].Concern != d[j].Concern {
			return d[i].Concern < d[j].Concern
		}
		if d[i].Method != d[j].Method {
			return d[i].Method < d[j].Method
		}
		return d[i].Resource < d[j].Resource
	})
}

// roundsDependencies keeps the records still live this review and flattens
// the open costs of their actions into the admitted-method demand
// (policy.ConstructionDemand). A record drops when its goal is no longer the
// need's active Episode, every one of its actions has settled (the
// dependency is satisfied, cancelled or replaced), or its evidence is
// older than a game day. The shortfall is measured against the stock of the
// moment by the demand itself.
func roundsDependencies(ctx context.Context, tx *sql.Tx, records []DependencyRecord, bindings []RoundsStandard, states []WorkOwner, tick domain.Tick) ([]DependencyRecord, []policy.AdmittedCost, error) {
	active := map[domain.ConcernID]WorkOwner{}
	for i, b := range bindings {
		active[b.Concern] = states[i]
	}
	var kept []DependencyRecord
	var admitted []policy.AdmittedCost
	for _, rec := range records {
		g, ok := active[rec.Need]
		if !ok || domain.ConcernID(g.OwnerID()) != rec.Concern || g.OwnerEpisode() != rec.Episode || !ownerActive(g) || tick < rec.Observed || tick-rec.Observed > domain.TicksPerDay {
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
		live := false
		for _, c := range rec.Costs {
			if open[c.Action] {
				live = true
				admitted = append(admitted, policy.AdmittedCost{Resource: rec.Resource, Action: c.Action, Count: c.Count})
			}
		}
		if live {
			kept = append(kept, rec)
		}
	}
	return kept, admitted, nil
}

// priorAdmitted is the last review's still-live admitted-method costs
// against its goal bindings, read before DetectRounds so an open shortfall
// raises its MaintainResource floor.
func priorAdmitted(ctx context.Context, tx *sql.Tx, previous Rounds, tick domain.Tick) ([]policy.AdmittedCost, error) {
	if len(previous.Dependencies) == 0 {
		return nil, nil
	}
	var bindings []RoundsStandard
	var states []WorkOwner
	for _, b := range previous.Standards {
		g, err := loadStandard(ctx, tx, b.Standard)
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
		bindings, states = append(bindings, RoundsStandard{Concern: b.Concern, Standard: domain.ConcernID(b.Project)}), append(states, g)
	}
	_, admitted, err := roundsDependencies(ctx, tx, previous.Dependencies, bindings, states, tick)
	return admitted, err
}
