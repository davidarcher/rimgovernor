package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

const ApparelPolicyRefused Reason = "apparel_policy_refused"

type ApparelPolicyFacts struct {
	Snapshot domain.GenerationSnapshot
	Tick     domain.Tick
	Accepted domain.Fact[bool]
}

type ApparelPolicyRequest struct {
	Action      domain.Action
	Progress    domain.Progress
	Current     domain.GenerationSnapshot
	MinimumTick domain.Tick
	Facts       ApparelPolicyFacts
}

func EvaluateApparelPolicy(r ApparelPolicyRequest) DraftDecision {
	refuse := func(reason Reason) DraftDecision {
		return DraftDecision{Refused: []Refusal{{Action: r.Action.ID(), Reason: reason}}}
	}
	value, ok := r.Action.ApparelPolicy()
	canonical, err := domain.NewApparelPolicyAction(r.Action.ID(), value)
	if !ok || err != nil || canonical != r.Action {
		return refuse(NotReady)
	}
	v := r.Progress.View()
	f := r.Facts
	if r.Current.Validate() != nil || r.Current.Native == 0 || !f.Snapshot.Matches(r.Current) || r.MinimumTick < 0 || f.Tick < r.MinimumTick {
		return refuse(StaleFacts)
	}
	if r.Progress.Action() != r.Action || v.Plan != r.Current.Plan || v.Revision != r.Current.Revision || v.Unresolved || (v.Stage != domain.Pending && v.Stage != domain.Prepared) {
		return refuse(NotReady)
	}
	if f.Tick < v.Tick || (v.Stage == domain.Prepared && !v.Snapshot.Matches(r.Current)) || (v.Attempt > 0 && (v.Snapshot.Colony != r.Current.Colony || v.Snapshot.Map != r.Current.Map || v.Snapshot.Load != r.Current.Load)) {
		return refuse(StaleFacts)
	}
	accepted, known := f.Accepted.Value()
	if !known {
		return refuse(UnknownFacts)
	}
	if !accepted {
		return refuse(NotReady)
	}
	return DraftDecision{Admitted: true}
}
