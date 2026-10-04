package policy

import "testing"

func TestNoOpReasonValidate(t *testing.T) {
	for _, r := range []NoOpReason{NoOpInputsUnknown, NoOpNotApplicable, NoOpSatisfied} {
		if err := r.Validate(); err != nil {
			t.Error(err)
		}
	}
	for _, r := range []NoOpReason{"", "vetoed"} {
		if r.Validate() == nil {
			t.Errorf("%q accepted", r)
		}
	}
}

func TestDetectRoutineRecordsNoOpReasons(t *testing.T) {
	r := needs(t, RoutineFacts{}, RoutineLatches{})
	reasons := map[ConcernID]NoOpReason{}
	for _, n := range r.NoOps {
		if err := n.Reason.Validate(); err != nil {
			t.Fatal(err)
		}
		reasons[n.Goal] = n.Reason
	}
	// Unmeasured inputs are unknown, never satisfied.
	if reasons[MaintainBabyFeeding] != NoOpInputsUnknown || reasons[MaintainPermits] != NoOpInputsUnknown {
		t.Error("unknown inputs not reported", reasons)
	}
	// A detector with no precondition files no assessment.
	if reasons[RecoverDisasterServices] != NoOpNotApplicable {
		t.Error("no disaster history", reasons[RecoverDisasterServices])
	}
	// A config-only goal with its option off is satisfied.
	if reasons[EnsureDefensiveLayout] != NoOpSatisfied {
		t.Error("defensive layout off", reasons[EnsureDefensiveLayout])
	}
	// A raised goal is not a no-op.
	for _, g := range r.Goals {
		if _, ok := reasons[g.ID]; ok {
			t.Errorf("%s raised and recorded as a no-op", g.ID)
		}
	}
}
