package telemetry

import "testing"

type countRecorder struct{ kinds []string }

func (c *countRecorder) Event(kind string, _ map[string]any, _ bool, _ map[string]any) (uint64, error) {
	c.kinds = append(c.kinds, kind)
	return uint64(len(c.kinds)), nil
}

func TestRouteExplanationsSplitsByKind(t *testing.T) {
	flight, explain := &countRecorder{}, &countRecorder{}
	r := RouteExplanations(flight, explain)
	for _, kind := range []string{"planner_step", ConcernTransitionKind, "dispatch", ConcernTransitionKind} {
		if _, err := r.Event(kind, nil, false, nil); err != nil {
			t.Fatal(err)
		}
	}
	if len(flight.kinds) != 2 || flight.kinds[0] != "planner_step" || flight.kinds[1] != "dispatch" {
		t.Fatalf("flight kinds = %v", flight.kinds)
	}
	if len(explain.kinds) != 2 || explain.kinds[0] != ConcernTransitionKind {
		t.Fatalf("explain kinds = %v", explain.kinds)
	}
	if RouteExplanations(flight, nil) != Recorder(flight) {
		t.Fatal("nil explain ring should leave the flight recorder unwrapped")
	}
}
