package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// WallRemovalTargetChanged is the resolve-time blocker: the exact
// native identity this step clears (the original wall, or the backup wall a
// preceding same-plan action built) no longer matches what was proposed.
const WallRemovalTargetChanged Reason = "wall_removal_target_changed"

// WallRemovalGeometryChanged is the site re-check blocker: native
// no longer reports this exact demolition/backup geometry as a legal,
// supported wall-upgrade site.
const WallRemovalGeometryChanged Reason = "wall_removal_geometry_changed"

type WallRemovalFacts struct {
	Snapshot        domain.GenerationSnapshot
	ObservationTick domain.Tick
	TargetIdentity  domain.Fact[string]
	SiteEligible    domain.Fact[bool]
}

type WallRemovalRequest struct {
	Action   domain.Action
	Progress domain.Progress
	Current  domain.GenerationSnapshot
	// BackupIdentity is the same-plan backup wall's proven native identity,
	// read from its own completed Progress. Empty for the main demolition,
	// where the removal's own Original() is the expected identity instead.
	BackupIdentity string
	Facts          WallRemovalFacts
}

// EvaluateWallRemoval re-validates one already-selected demolition or backup
// removal immediately before dispatch, the same shape EvaluateHomeCoverage
// uses. Admission proves the exact native occupant and site geometry still
// match what was proposed; it does not prove the demolition will be accepted
// or observed complete.
func EvaluateWallRemoval(r WallRemovalRequest) DraftDecision {
	refuse := func(reason Reason) DraftDecision {
		return DraftDecision{Refused: []Refusal{{Action: r.Action.ID(), Reason: reason}}}
	}
	removal, ok := r.Action.WallRemoval()
	canonical, err := domain.NewWallRemovalAction(r.Action.ID(), removal)
	if !ok || err != nil || canonical != r.Action {
		return refuse(NotReady)
	}
	v := r.Progress.View()
	f := r.Facts
	if r.Current.Validate() != nil || r.Current.Native == 0 || !f.Snapshot.Matches(r.Current) {
		return refuse(StaleFacts)
	}
	if r.Progress.Action() != r.Action || v.Plan != r.Current.Plan || v.Revision != r.Current.Revision || v.Unresolved || (v.Stage != domain.Pending && v.Stage != domain.Prepared) {
		return refuse(NotReady)
	}
	if f.ObservationTick < v.Tick || (v.Stage == domain.Prepared && !v.Snapshot.Matches(r.Current)) || (v.Attempt > 0 && (v.Snapshot.Colony != r.Current.Colony || v.Snapshot.Map != r.Current.Map || v.Snapshot.Load != r.Current.Load)) {
		return refuse(StaleFacts)
	}
	expected := removal.Original()
	if removal.BackupOf() != "" {
		expected = r.BackupIdentity
	}
	identity, known := f.TargetIdentity.Value()
	if !known {
		return refuse(UnknownFacts)
	}
	if expected == "" || identity != expected {
		return refuse(WallRemovalTargetChanged)
	}
	eligible, known := f.SiteEligible.Value()
	if !known {
		return refuse(UnknownFacts)
	}
	if !eligible {
		return refuse(WallRemovalGeometryChanged)
	}
	return DraftDecision{Admitted: true}
}
