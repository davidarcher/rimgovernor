package buildingruntime

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

func (r *Rounder) reformChoice(read observation.RoundsReading, snapshot domain.GenerationSnapshot) domain.Fact[policy.DesignChoice] {
	ideo, known := read.Projection.Facts.Ideology.Value()
	if !known || read.Frame.Catalog == nil || read.Frame.Pawns == nil {
		return domain.Unknown[policy.DesignChoice]()
	}
	faction := ""
	for _, pawn := range read.Frame.Pawns.Pawns {
		if !pawn.GetFreeColonist() || pawn.GetDead() {
			continue
		}
		name := pawn.GetStanding().GetFactionDefName()
		if name == "" || (faction != "" && faction != name) {
			return domain.Unknown[policy.DesignChoice]()
		}
		faction = name
	}
	options, err := read.Frame.Catalog.IdeoligionOptions(faction)
	if err != nil {
		return domain.Unknown[policy.DesignChoice]()
	}
	options.Needs = policy.IdeoligionNeeds(ideo.Facts, read.Projection.WorkPawns, read.Projection.Facts.MoodPawns)
	current := domain.IdeoligionDesign{Memes: ideo.Facts.Memes, Fluid: true}
	for _, held := range ideo.Facts.Precepts {
		def := bridge.DefRow[*d.PreceptDef](read.Frame.Catalog, held.Def)
		if def == nil {
			return domain.Unknown[policy.DesignChoice]()
		}
		if def.GetPreceptClass() == "RimWorld.Precept" {
			current.Precepts = append(current.Precepts, held.Def)
		}
	}
	calm := domain.Unknown[bool]()
	tick := read.Projection.Identity.Tick
	if emergency, err := policy.NewEmergencySnapshot(snapshot, tick, read.Emergency); err == nil {
		hostiles, patients := policy.EmergencyNeeds(emergency, snapshot, tick)
		calm = policy.RitualCalm(hostiles, patients)
	}
	return policy.ChooseIdeoligionReform(options, current, ideo.Facts.Development, calm)
}

type ideoligionRecord struct {
	Ideo             string
	Expected, Target domain.IdeoligionDesign
	Count            int
}

func reformRecord(record string) (domain.IdeoligionReform, error) {
	var v ideoligionRecord
	if err := json.Unmarshal([]byte(record), &v); err != nil {
		return domain.IdeoligionReform{}, err
	}
	return domain.NewIdeoligionReform(v.Ideo, v.Expected, v.Target, v.Count)
}

func observedReform(ideo policy.Ideoligion, actual domain.IdeoligionDesign, intent domain.IdeoligionReform) domain.Fact[bool] {
	state, known := ideo.Facts.Development.Value()
	if !known || ideo.Facts.IdeoID != intent.IdeoID() {
		return domain.Unknown[bool]()
	}
	if state.ReformCount != intent.Count()+1 {
		return domain.Known(false)
	}
	actual.Fluid = state.Fluid
	canonical, err := domain.CanonicalIdeoligionDesign(actual)
	if err != nil {
		return domain.Unknown[bool]()
	}
	a, _ := json.Marshal(canonical)
	b, _ := json.Marshal(intent.Design())
	return domain.Known(string(a) == string(b))
}

func (r *Rounder) reviewIdeoligion(ctx context.Context, read *observation.RoundsReading, snapshot domain.GenerationSnapshot) error {
	choice := r.reformChoice(*read, snapshot)
	v, known := choice.Value()
	read.Projection.Facts.ReformOwed = domain.Unknown[bool]()
	if known {
		read.Projection.Facts.ReformOwed = domain.Known(len(v.Design.Memes) > 0)
	}
	review, err := r.player.journal.LoadRounds(ctx)
	if errors.Is(err, store.ErrNotFound) {
		return nil
	}
	if err != nil {
		return err
	}
	if !review.Snapshot.Matches(snapshot) {
		return nil
	}
	id := domain.ConcernID("")
	for _, binding := range review.Standards {
		if binding.Concern == policy.ImproveIdeoligion {
			id = binding.Standard
		}
	}
	if id == "" {
		return nil
	}
	standard, err := r.player.journal.LoadStandard(ctx, id)
	if errors.Is(err, store.ErrNotFound) {
		return nil
	}
	if err != nil {
		return err
	}
	if standard.Standard.Record != "" {
		intent, err := reformRecord(standard.Standard.Record)
		if err != nil {
			return err
		}
		ideo, known := read.Projection.Facts.Ideology.Value()
		if !known {
			read.Projection.Facts.ReformOwed = domain.Unknown[bool]()
			return nil
		}
		actual := domain.IdeoligionDesign{Memes: ideo.Facts.Memes, Fluid: true}
		if read.Frame.Catalog == nil {
			read.Projection.Facts.ReformOwed = domain.Unknown[bool]()
			return nil
		}
		for _, held := range ideo.Facts.Precepts {
			def := bridge.DefRow[*d.PreceptDef](read.Frame.Catalog, held.Def)
			if def == nil {
				read.Projection.Facts.ReformOwed = domain.Unknown[bool]()
				return nil
			}
			if def.GetPreceptClass() == "RimWorld.Precept" {
				actual.Precepts = append(actual.Precepts, held.Def)
			}
		}
		complete, known := observedReform(ideo, actual, intent).Value()
		if known && complete {
			read.Projection.Facts.ReformOwed = domain.Known(false)
			_, err = r.player.journal.RecordStandard(ctx, standard.Standard.ID, standard.Revision, "")
			return err
		}
		if state, known := ideo.Facts.Development.Value(); known {
			a, _ := domain.CanonicalIdeoligionDesign(actual)
			b, _ := json.Marshal(a)
			expected, _ := json.Marshal(intent.Expected())
			if state.ReformCount != intent.Count() || string(b) != string(expected) || ideo.Facts.IdeoID != intent.IdeoID() {
				_, err = r.player.journal.RecordStandard(ctx, standard.Standard.ID, standard.Revision, "")
				return err
			}
		}
	}
	return nil
}

// The method journals the expected state and target before Hands dispatches.
// Re-reading eligibility and the native reform counter makes recovery safe.
type RoundsIdeoligionPlanner struct{ reviewer *Rounder }
type RoundsIdeoligionResult struct {
	Verdict
	Plan       domain.PlanID
	Comparison *policy.DesignComparison
}

func NewRoundsIdeoligionPlanner(r *Rounder) (*RoundsIdeoligionPlanner, error) {
	if r == nil || !r.methodEnabled(policy.ImproveIdeoligion) {
		return nil, fmt.Errorf("%w: ideoligion reviewer unavailable", ErrControl)
	}
	return &RoundsIdeoligionPlanner{r}, nil
}
func (r *RoundsIdeoligionPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoundsIdeoligionResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoundsIdeoligionResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoundsIdeoligionResult{}, ErrControl
	}
	review, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoundsIdeoligionResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return RoundsIdeoligionResult{Verdict: BuildingReasonNoReview}, nil
	}
	concern, workable, err := p.journal.Workable(call, review, policy.ImproveIdeoligion)
	if err != nil {
		return RoundsIdeoligionResult{}, err
	}
	if !workable {
		return RoundsIdeoligionResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	for _, method := range concern.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoundsIdeoligionResult{}, err
		}
		if store.PlanOpen(plan) {
			return RoundsIdeoligionResult{Verdict: BuildingReasonExistingWork}, nil
		}
	}
	expected, err := stepScope(call, r.reviewer.native)
	if err != nil {
		return RoundsIdeoligionResult{}, err
	}
	if !roundsBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoundsIdeoligionResult{}, ErrControl
	}
	started := r.reviewer.clock.Now()
	read, err := r.reviewer.observeOwned(call, r.reviewer.native, expected, domain.Unknown[[]policy.ConstructionClaim]())
	if err != nil {
		return RoundsIdeoligionResult{}, err
	}
	choice, known := r.reviewer.reformChoice(read, state.Snapshot).Value()
	if !known || len(choice.Design.Memes) == 0 {
		return RoundsIdeoligionResult{Verdict: waitFor(policy.CauseMethodUsed, "ideoligion_design"), Comparison: choice.Comparison}, nil
	}
	ideo, _ := read.Projection.Facts.Ideology.Value()
	development, _ := ideo.Facts.Development.Value()
	current := domain.IdeoligionDesign{Memes: ideo.Facts.Memes, Fluid: true}
	for _, held := range ideo.Facts.Precepts {
		def := bridge.DefRow[*d.PreceptDef](read.Frame.Catalog, held.Def)
		if def != nil && def.GetPreceptClass() == "RimWorld.Precept" {
			current.Precepts = append(current.Precepts, held.Def)
		}
	}
	prefix := fmt.Sprintf("ideoligion-%d-", development.ReformCount)
	method, verdict, ok, err := admitStandardMethod(call, p.journal, concern, prefix, state.Snapshot)
	if err != nil {
		return RoundsIdeoligionResult{}, err
	}
	if !ok {
		return RoundsIdeoligionResult{Verdict: verdict}, nil
	}
	intent, err := domain.NewIdeoligionReform(ideo.Facts.IdeoID, current, choice.Design, development.ReformCount)
	if err != nil {
		return RoundsIdeoligionResult{}, err
	}
	if concern.Standard.Record != "" {
		pending, err := reformRecord(concern.Standard.Record)
		if err != nil {
			return RoundsIdeoligionResult{}, err
		}
		if complete, known := observedReform(ideo, current, pending).Value(); known && complete {
			return RoundsIdeoligionResult{Verdict: BuildingReasonNoDeficit}, nil
		}
		actual, _ := domain.CanonicalIdeoligionDesign(current)
		a, _ := json.Marshal(actual)
		b, _ := json.Marshal(pending.Expected())
		target, _ := json.Marshal(pending.Design())
		selected, _ := json.Marshal(choice.Design)
		if pending.IdeoID() == ideo.Facts.IdeoID && pending.Count() == development.ReformCount && string(a) == string(b) && string(target) == string(selected) {
			intent = pending
		}
	}
	id := domain.MintPlanID()
	action, err := domain.NewIdeoligionReformAction(domain.ActionID(fmt.Sprintf("%s-0", id)), intent)
	if err != nil {
		return RoundsIdeoligionResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoundsIdeoligionResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoundsIdeoligionResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoundsIdeoligionResult{}, ErrControl
	}
	record, err := json.Marshal(ideoligionRecord{intent.IdeoID(), intent.Expected(), intent.Design(), intent.Count()})
	if err != nil {
		return RoundsIdeoligionResult{}, err
	}
	comparison := choice.Comparison
	reason := fmt.Sprintf("ideoligion: restrictions %d→%d, mood cost %g→%g, obligations %d→%d; work relief %t, observed mood relief %g, new mood cost %g", comparison.Current.Restrictions, comparison.Candidate.Restrictions, comparison.Current.MoodCost, comparison.Candidate.MoodCost, comparison.Current.Obligations, comparison.Candidate.Obligations, comparison.WorkRelief, comparison.MoodRelief, comparison.NewMoodCost)
	if _, err = p.journal.CommitMethodRecord(call, concern.Standard.ID, concern.Revision, method, reason, plan, string(record)); err != nil {
		return RoundsIdeoligionResult{}, err
	}
	return RoundsIdeoligionResult{Verdict: BuildingReasonAdmitted, Plan: id, Comparison: comparison}, nil
}
