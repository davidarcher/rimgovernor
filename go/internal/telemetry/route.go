package telemetry

// ConcernTransitionKind is the explanation row emitted when a concern's typed
// reason changes (fields: flight-rows.md, Explanation rows).
const ConcernTransitionKind = "concern_transition"

// explanationKinds are the kinds that belong to the player-facing explanation
// stream (explain.jsonl) rather than the developer flight stream.
var explanationKinds = map[string]bool{ConcernTransitionKind: true}

// IsExplanationKind reports whether rows of kind go to the explanation ring.
func IsExplanationKind(kind string) bool { return explanationKinds[kind] }

// RouteExplanations returns a Recorder that writes explanation kinds to
// explain and every other kind to flight, so Decide and slog call sites need
// not know which ring a kind lands in. A nil explain sends everything to flight.
func RouteExplanations(flight, explain Recorder) Recorder {
	if explain == nil {
		return flight
	}
	return kindRouter{flight: flight, explain: explain}
}

type kindRouter struct{ flight, explain Recorder }

func (r kindRouter) Event(kind string, context map[string]any, durable bool, payload map[string]any) (uint64, error) {
	if IsExplanationKind(kind) {
		return r.explain.Event(kind, context, durable, payload)
	}
	return r.flight.Event(kind, context, durable, payload)
}
