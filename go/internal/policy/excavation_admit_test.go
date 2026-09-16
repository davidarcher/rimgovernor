package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func excavationRequest(t *testing.T) ExcavationRequest {
	t.Helper()
	excavation, _ := domain.NewExcavation(domain.Cell{X: 4, Z: 5}, "Granite")
	a, _ := domain.NewExcavationAction("dig", excavation)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	s := domain.GenerationSnapshot{Colony: "colony", Map: 0, Load: "load", Direction: 1, Plan: plan.ID(), Revision: 1, Native: 1}
	p, _ := domain.NewProgress(plan, a.ID())
	facts := ExcavationFacts{
		Snapshot: s, ObservationTick: 12,
		Fogged:          domain.Known(false),
		Definition:      domain.Known("Granite"),
		Eligible:        domain.Known(true),
		Support:         ExcavationSupportSupported,
		WorkerAvailable: domain.Known(true), AccessReachable: domain.Known(true),
	}
	return ExcavationRequest{Action: a, Progress: p, Current: s, Facts: facts}
}

func TestExcavationAdmission(t *testing.T) {
	r := excavationRequest(t)
	for _, prepared := range []bool{false, true} {
		if prepared {
			var err error
			r.Progress, err = r.Progress.Prepare(r.Current, 11)
			if err != nil {
				t.Fatal(err)
			}
		}
		if d := EvaluateExcavation(r); !d.Admitted || len(d.Refused) != 0 {
			t.Fatal(prepared, d)
		}
	}
}

func TestExcavationRefusals(t *testing.T) {
	cases := map[string]struct {
		edit   func(*ExcavationRequest)
		reason Reason
	}{
		"fogged":             {func(r *ExcavationRequest) { r.Facts.Fogged = domain.Known(true) }, UnknownFacts},
		"fog unknown":        {func(r *ExcavationRequest) { r.Facts.Fogged = domain.Unknown[bool]() }, UnknownFacts},
		"definition unknown": {func(r *ExcavationRequest) { r.Facts.Definition = domain.Unknown[string]() }, UnknownFacts},
		"rock gone":          {func(r *ExcavationRequest) { r.Facts.Definition = domain.Known("") }, ExcavationGeometryChanged},
		"other rock":         {func(r *ExcavationRequest) { r.Facts.Definition = domain.Known("Marble") }, ExcavationGeometryChanged},
		"ineligible":         {func(r *ExcavationRequest) { r.Facts.Eligible = domain.Known(false) }, ExcavationGeometryChanged},
		"unsupported":        {func(r *ExcavationRequest) { r.Facts.Support = ExcavationSupportUnsupported }, ExcavationUnsupported},
		"support unknown":    {func(r *ExcavationRequest) { r.Facts.Support = ExcavationSupportUnknown }, UnknownFacts},
		"no worker":          {func(r *ExcavationRequest) { r.Facts.WorkerAvailable = domain.Known(false) }, NotReady},
		"no access":          {func(r *ExcavationRequest) { r.Facts.AccessReachable = domain.Known(false) }, NotReady},
		"worker unknown":     {func(r *ExcavationRequest) { r.Facts.WorkerAvailable = domain.Unknown[bool]() }, UnknownFacts},
		"stale facts":        {func(r *ExcavationRequest) { r.Facts.Snapshot.Native = 2 }, StaleFacts},
		"old observation": {func(r *ExcavationRequest) {
			r.Facts.ObservationTick = 0
			r.Progress, _ = r.Progress.Prepare(r.Current, 11)
		}, StaleFacts},
		"wrong plan": {func(r *ExcavationRequest) { r.Current.Plan = "other"; r.Facts.Snapshot.Plan = "other" }, NotReady},
	}
	for name, c := range cases {
		t.Run(name, func(t *testing.T) {
			r := excavationRequest(t)
			c.edit(&r)
			d := EvaluateExcavation(r)
			if d.Admitted || len(d.Refused) != 1 || d.Refused[0].Reason != c.reason || d.Refused[0].Action != "dig" {
				t.Fatal(d)
			}
		})
	}
}

func TestExcavationReasonsHaveHeldCounterparts(t *testing.T) {
	for _, reason := range []Reason{ExcavationUnsupported, ExcavationGeometryChanged} {
		if string(reason) != string(domain.HeldExcavationUnsupported) && string(reason) != string(domain.HeldExcavationGeometryChanged) {
			t.Fatal(reason)
		}
	}
}
