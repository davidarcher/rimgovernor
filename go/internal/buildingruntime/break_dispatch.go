package buildingruntime

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

type BreakResponseSource interface {
	ReadEmergency(context.Context, *c.Identity) (bridge.EmergencyObservation, bridge.Result, error)
	ReadCombatPawns(context.Context, *c.Identity, []string) (*n.ListPawnsReply, bridge.Result, error)
}

func breakCell(row *n.PawnState) domain.Fact[domain.Cell] {
	p := row.GetPawn().GetPosition()
	if p == nil || p.X == nil || p.Z == nil {
		return domain.Unknown[domain.Cell]()
	}
	return domain.Known(domain.Cell{X: p.GetX(), Z: p.GetZ()})
}

// Release the breaking pawn's action claims through ordinary cancellation;
// dispatched attempts remain reconcilable and owned drafts retain cleanup.
func releaseBreakWork(ctx context.Context, journal *store.Store, current domain.GenerationSnapshot, f policy.EmergencyFacts, plans []store.PlanState) error {
	broken := map[domain.PawnID]bool{}
	aggressive := map[domain.PawnID]bool{}
	for _, p := range f.Colonists {
		if policy.AggressiveBreak(p) {
			aggressive[domain.PawnID(p.ID)] = true
		}
		if _, active := p.MentalState.Value(); active {
			broken[domain.PawnID(p.ID)] = true
		}
	}
	complete, ck := f.ColonistsComplete.Value()
	for _, plan := range plans {
		if plan.Retired {
			continue
		}
		if len(broken) == 0 {
			hasSubdue := false
			for _, action := range plan.Spec.Actions() {
				hasSubdue = hasSubdue || action.Subdues()
			}
			if !hasSubdue {
				continue
			}
		}
		sites, err := journal.DispatchSites(ctx, plan.Spec.ID())
		if err != nil {
			return err
		}
		for _, p := range plan.Progress {
			v := p.View()
			if m, ok := p.Action().MeleeAttack(); ok && m.Subdue() && ck && complete && !aggressive[m.Target()] && !v.Unresolved && (v.Stage == domain.Pending || v.Stage == domain.Prepared) {
				if _, err = journal.Cancel(ctx, plan.Spec.ID(), v.Action); err != nil {
					return err
				}
				continue
			}
			if !broken[sites[v.Action].Worker] || v.Stage == domain.Completed || v.Stage == domain.Cancelled || v.Stage == domain.Unsuccessful {
				continue
			}
			if v.Attempt > 0 && !v.Snapshot.SameWorld(current) {
				continue
			}
			if _, err = journal.Cancel(ctx, plan.Spec.ID(), v.Action); err != nil {
				return err
			}
		}
	}
	return nil
}

// Read one fresh census for the worker step. Cleanup and uncertain outcomes are
// never fenced: only new dispatch is subject to the exclusion radius.
func (w *Worker) breakDispatchHolds(ctx context.Context, current domain.GenerationSnapshot, plans []store.PlanState) (map[domain.ActionID]bool, error) {
	held := map[domain.ActionID]bool{}
	if w.config.BreakSource == nil {
		return held, nil
	}
	census, _, err := w.config.BreakSource.ReadEmergency(ctx, boundary.Identity(current))
	if err != nil {
		return nil, err
	}
	if _, err = boundary.Context(census.Context, current); err != nil {
		return nil, err
	}
	if complete, known := census.Facts.ColonistsComplete.Value(); !known || !complete {
		return nil, ErrControl
	}
	var ids []string
	broken := map[domain.PawnID]bool{}
	var aggressive []policy.PawnID
	for _, p := range census.Facts.Colonists {
		ids = append(ids, string(p.ID))
		if _, active := p.MentalState.Value(); active {
			broken[domain.PawnID(p.ID)] = true
		}
		if policy.AggressiveBreak(p) {
			aggressive = append(aggressive, p.ID)
		}
	}
	if len(broken) == 0 {
		return held, nil
	}
	positions := map[string]domain.Fact[domain.Cell]{}
	if len(aggressive) > 0 {
		reply, _, err := w.config.BreakSource.ReadCombatPawns(ctx, boundary.Identity(current), ids)
		if err != nil {
			return nil, err
		}
		observed := reply.GetObserved()
		if observed == nil {
			return nil, ErrControl
		}
		counts := observed.Completeness
		if counts == nil || counts.Page == nil || !counts.Page.GetComplete() || counts.Page.GetNextCursor() != "" || counts.Unreadable == nil || counts.GetUnreadable() != 0 || counts.Matched == nil || counts.Returned == nil || counts.GetMatched() != uint64(len(ids)) || counts.GetReturned() != uint64(len(observed.Pawns)) || len(observed.Pawns) != len(ids) {
			return nil, ErrControl
		}
		if _, err = boundary.Context(observed.Context, current); err != nil {
			return nil, err
		}
		if !domain.Tick(observed.Context.GetTick()).Covers(domain.Tick(census.Context.GetTick())) {
			return nil, ErrControl
		}
		for _, row := range observed.Pawns {
			positions[row.GetPawn().GetId()] = breakCell(row)
		}
	}
	for _, plan := range plans {
		sites, err := w.player.journal.DispatchSites(ctx, plan.Spec.ID())
		if err != nil {
			return nil, err
		}
		drafts := map[domain.ActionID]bool{}
		for _, action := range plan.Spec.Actions() {
			if action.Subdues() {
				m, _ := action.MeleeAttack()
				drafts[m.DraftAction()] = true
			}
		}
		for _, action := range plan.Spec.Actions() {
			site := sites[action.ID()]
			if broken[site.Worker] {
				held[action.ID()] = true
				continue
			}
			if action.Subdues() || drafts[action.ID()] {
				continue
			}
			for _, id := range aggressive {
				at, known := positions[string(id)].Value()
				if !known {
					held[action.ID()] = true
					break
				}
				// An explicit target within the exclusion zone is unsafe even when its
				// assigned worker starts outside it. Unknown locations hold conservatively.
				cells := []domain.Fact[domain.Cell]{site.Cell}
				if site.Worker != "" {
					if _, known := positions[string(site.Worker)].Value(); !known {
						held[action.ID()] = true
					}
					cells = append(cells, positions[string(site.Worker)])
				}
				if p, ok := positions[site.Target]; ok {
					cells = append(cells, p)
				}
				located := false
				for _, cell := range cells {
					if p, k := cell.Value(); k {
						located = true
						if policy.InBreakRadius(p, at) {
							held[action.ID()] = true
						}
					}
				}
				if !located {
					held[action.ID()] = true
				}
			}
		}
	}
	return held, nil
}
