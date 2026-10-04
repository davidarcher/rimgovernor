package store

import (
	"context"
	"database/sql"
	"errors"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// WorkOwner is the goal or Project a shared building planner binds methods
// to (#1928). The building ladder is the same code for a Standard goal and
// for the Project kinds it serves (cooking, butcher, power, research shelter,
// defense dig), so those planners hold the owner through this interface
// instead of a goal handle. GoalState and ProjectState implement it; a
// planner that serves only one concept takes that concept's state.
type WorkOwner interface {
	methodOwner
	// OwnerID is the goal's or Project's row id.
	OwnerID() string
	// OwnerEpoch is the goal's current epoch, 0 for a Project.
	OwnerEpoch() uint64
	// OwnerRevision is the local CAS token method commits are scoped to.
	OwnerRevision() uint64
	// OwnerMethods lists the open methods; OwnerHistory every method of
	// the current epoch, retired plans included.
	OwnerMethods() []domain.GoalMethod
	OwnerHistory() []domain.GoalMethod
	// OwnerDeficit reports an active or open owner whose review found a
	// deficit: the state a planner may work.
	OwnerDeficit() bool
	// OwnerPriority is the owner's priority, 0 (highest) to 4.
	OwnerPriority() int
}

func (g GoalState) OwnerID() string                      { return string(g.Goal.ID) }
func (g GoalState) OwnerEpoch() uint64                   { return g.Goal.Epoch }
func (g GoalState) OwnerRevision() uint64                { return g.Revision }
func (g GoalState) OwnerMethods() []domain.GoalMethod    { return g.Methods }
func (g GoalState) OwnerHistory() []domain.GoalMethod    { return g.History }
func (p ProjectState) OwnerID() string                   { return string(p.Project.ID) }
func (p ProjectState) OwnerEpoch() uint64                { return 0 }
func (p ProjectState) OwnerRevision() uint64             { return p.Revision }
func (p ProjectState) OwnerMethods() []domain.GoalMethod { return p.goalMethods(p.Methods) }
func (p ProjectState) OwnerHistory() []domain.GoalMethod { return p.goalMethods(p.History) }

func (g GoalState) OwnerPriority() int    { return g.Goal.Priority }
func (p ProjectState) OwnerPriority() int { return p.Project.Priority }

func (g GoalState) OwnerDeficit() bool {
	return g.Goal.Status == domain.GoalActive && g.Goal.Need == domain.NeedDeficit
}
func (p ProjectState) OwnerDeficit() bool {
	return p.Project.Status == domain.ProjectOpen && p.Project.Need == domain.NeedDeficit
}

func (p ProjectState) goalMethods(rows []ProjectMethod) []domain.GoalMethod {
	var out []domain.GoalMethod
	for _, m := range rows {
		out = append(out, domain.GoalMethod{Goal: domain.ConcernID(p.Project.ID), Method: m.Method, Plan: m.Plan})
	}
	return out
}

// WorkableOwner is Workable for a need that may be a Project's: it loads the
// goal or Project the review binds to need and reports whether a planner may
// work it. The owner is returned whenever the review binds one.
func (s *Store) WorkableOwner(ctx context.Context, r Rounds, need policy.ConcernID) (WorkOwner, bool, error) {
	for _, binding := range r.Projects {
		if binding.Need == need {
			p, ok, err := s.WorkableProject(ctx, r, need)
			return p, ok, err
		}
	}
	g, ok, err := s.Workable(ctx, r, need)
	return g, ok, err
}

// WorkableProject loads the Project the review binds to need and reports
// whether a planner may work it: an open deficit the Safeguards admit (#1121).
func (s *Store) WorkableProject(ctx context.Context, r Rounds, need policy.ConcernID) (ProjectState, bool, error) {
	for _, binding := range r.Projects {
		if binding.Need != need {
			continue
		}
		p, err := s.LoadProject(ctx, binding.Project)
		if err != nil {
			return ProjectState{}, false, err
		}
		return p, p.Project.Status == domain.ProjectOpen && p.Project.Need == domain.NeedDeficit && r.VetoProject(p.Project) == "", nil
	}
	return ProjectState{}, false, nil
}

// VetoOwner is Veto for the goal or Project the owner is.
func (r Rounds) VetoOwner(owner WorkOwner) string {
	need, bound := owner.ownerNeed(r)
	if !bound {
		return ""
	}
	return r.vetoNeed(need, owner.ownerPriority())
}

// VetoProject is Veto for a Project.
func (r Rounds) VetoProject(p domain.Project) string {
	need, bound := r.projectNeed(p.ID)
	if !bound {
		return ""
	}
	return r.vetoNeed(need, p.Priority)
}

// ProjectFor returns the Project the review binds to need.
func (r Rounds) ProjectFor(need policy.ConcernID) (domain.ProjectID, bool) {
	for _, binding := range r.Projects {
		if binding.Need == need {
			return binding.Project, true
		}
	}
	return "", false
}

// CommitOwnerMethod stores a method for the owner like CommitGoalMethodReason
// or CommitProjectMethod, whichever the owner is.
func (s *Store) CommitOwnerMethod(ctx context.Context, owner WorkOwner, method domain.MethodID, reason string, plan domain.PlanSpec) error {
	switch o := owner.(type) {
	case GoalState:
		_, err := s.CommitGoalMethodReason(ctx, o.Goal.ID, o.Revision, method, reason, plan)
		return err
	case ProjectState:
		_, err := s.CommitProjectMethod(ctx, o.Project.ID, o.Revision, method, reason, plan)
		return err
	}
	return fmt.Errorf("unsupported method owner %T", owner)
}

// LoadOwnerMethod reads the owner's binding of method in its current epoch,
// including a retired plan's.
func (s *Store) LoadOwnerMethod(ctx context.Context, owner WorkOwner, method domain.MethodID) (domain.GoalMethod, error) {
	switch o := owner.(type) {
	case GoalState:
		return s.LoadGoalMethod(ctx, o.Goal.ID, o.Goal.Epoch, method)
	case ProjectState:
		var plan domain.PlanID
		err := s.db.QueryRowContext(ctx, "SELECT plan_id FROM goal_methods WHERE project_id=? AND method_id=?", o.Project.ID, method).Scan(&plan)
		if errors.Is(err, sql.ErrNoRows) {
			return domain.GoalMethod{}, ErrNotFound
		}
		return domain.GoalMethod{Goal: domain.ConcernID(o.Project.ID), Method: method, Plan: plan}, err
	}
	return domain.GoalMethod{}, fmt.Errorf("unsupported method owner %T", owner)
}

// LoadOwnerMethods lists the owner's methods in its current epoch, retired
// bindings included.
func (s *Store) LoadOwnerMethods(ctx context.Context, owner WorkOwner) ([]domain.GoalMethod, error) {
	switch o := owner.(type) {
	case GoalState:
		return s.LoadGoalMethods(ctx, o.Goal.ID, o.Goal.Epoch)
	case ProjectState:
		p, err := s.LoadProject(ctx, o.Project.ID)
		if err != nil {
			return nil, err
		}
		return p.OwnerHistory(), nil
	}
	return nil, fmt.Errorf("unsupported method owner %T", owner)
}

// reloadOwner reads the owner's row again inside tx.
func reloadOwner(ctx context.Context, tx *sql.Tx, owner WorkOwner) (WorkOwner, error) {
	switch o := owner.(type) {
	case GoalState:
		return loadGoal(ctx, tx, o.Goal.ID)
	case ProjectState:
		return loadProject(ctx, tx, o.Project.ID)
	}
	return nil, fmt.Errorf("unsupported method owner %T", owner)
}

// ownerTick is the tick the owner was last reviewed at.
func ownerTick(owner WorkOwner) domain.Tick {
	switch o := owner.(type) {
	case GoalState:
		return o.Goal.Tick
	case ProjectState:
		return o.Project.Tick
	}
	return 0
}

// commitOwnerMethod is CommitOwnerMethod inside tx, returning the owner as
// committed.
func commitOwnerMethod(ctx context.Context, tx *sql.Tx, owner WorkOwner, method domain.MethodID, reason string, plan domain.PlanSpec) (WorkOwner, error) {
	switch o := owner.(type) {
	case GoalState:
		return commitGoalMethod(ctx, tx, o.Goal.ID, o.Revision, method, reason, plan)
	case ProjectState:
		return commitProjectMethod(ctx, tx, o.Project.ID, o.Revision, method, reason, plan)
	}
	return nil, fmt.Errorf("unsupported method owner %T", owner)
}

func decisionOf(owner WorkOwner, refused []policy.Refusal) BuildingMethodDecision {
	d := BuildingMethodDecision{Refused: refused}
	switch o := owner.(type) {
	case GoalState:
		d.Goal = o
	case ProjectState:
		d.Project = o
	}
	return d
}

// OwnerSummary is the lifecycle view of a goal or Project the HTTP API
// reports. A Project's status maps onto the goal vocabulary
// the wire already uses (open is active, finished is satisfied); Epoch is 0
// for a Project, which has none.
type OwnerSummary struct {
	ID       string
	Status   domain.GoalStatus
	Need     domain.NeedState
	Priority int
	Epoch    uint64
	Revision uint64
	Tick     domain.Tick
	Snapshot domain.GenerationSnapshot
	Retired  bool
}

// SummarizeOwner reads the owner's lifecycle summary. It reports false for an
// owner that is neither a goal nor a Project.
func SummarizeOwner(owner WorkOwner) (OwnerSummary, bool) {
	switch o := owner.(type) {
	case GoalState:
		g := o.Goal
		return OwnerSummary{string(g.ID), g.Status, g.Need, g.Priority, g.Epoch, o.Revision, g.Tick, g.Snapshot, o.Retired}, true
	case ProjectState:
		p := o.Project
		status := domain.GoalActive
		switch p.Status {
		case domain.ProjectFinished:
			status = domain.GoalSatisfied
		case domain.ProjectInvalidated:
			status = domain.GoalInvalidated
		}
		return OwnerSummary{string(p.ID), status, p.Need, p.Priority, 0, o.Revision, p.Tick, p.Snapshot, o.Retired}, true
	}
	return OwnerSummary{}, false
}

// LoadOwner loads the goal or Project a recorded identity names: a Project
// row's id carries the projects table's prefix.
func (s *Store) LoadOwner(ctx context.Context, id string) (WorkOwner, error) {
	if isProjectID(id) {
		return s.LoadProject(ctx, domain.ProjectID(id))
	}
	return s.LoadGoal(ctx, domain.ConcernID(id))
}

// loadOwner is LoadOwner inside tx.
func loadOwner(ctx context.Context, tx *sql.Tx, id string) (WorkOwner, error) {
	if isProjectID(id) {
		return loadProject(ctx, tx, domain.ProjectID(id))
	}
	return loadGoal(ctx, tx, domain.ConcernID(id))
}

// Owner is the goal or Project the method was admitted for.
func (d BuildingMethodDecision) Owner() WorkOwner {
	if d.Project.Project.ID != "" {
		return d.Project
	}
	return d.Goal
}

// ownerActive reports an owner that is active in the goal vocabulary: an open
// Project or an active goal.
func ownerActive(owner WorkOwner) bool {
	summary, ok := SummarizeOwner(owner)
	return ok && summary.Status == domain.GoalActive
}
