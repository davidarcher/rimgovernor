package policy

// MaintainClimateEnvelope is the Shelter department's plan-wide climate
// envelope (#2962): every planned room's missing vents and roofs,
// whichever owner (or none) reconciles
// the room. An owner builds a room's vents and roofs only while its own
// mismatch check holds (reconcileRoom), so a room whose owner has stopped
// reconciling would never get them; this Standard asks the plan itself. It is a
// Standard whose target is nothing owed: a vent over a standing ring wall
// (OpVentIn) or a roofed-set cell still open to the sky once the ring stands
// (OpRoofIn). The build rides the reconcile library's two operations, so a
// vent is absent while its def is unavailable, on rock or already standing, and
// a roof is never owed an outdoor or unfenced room.
const MaintainClimateEnvelope ConcernID = "MaintainClimateEnvelope"

// climateEnvelopePriority ranks MaintainClimateEnvelope with the other
// priority-3 chores.
const climateEnvelopePriority = 3

// inspectClimateEnvelope raises MaintainClimateEnvelope while a planned vent or
// roof is owed; unknown raises nothing.
func inspectClimateEnvelope(c *roundsRun) error {
	c.owed(MaintainClimateEnvelope, climateEnvelopePriority, c.f.EnvelopeOwed)
	return nil
}

// EnvelopeOps keeps the vents and roofs of a room's ready operations: the
// plan-wide envelope owes the room nothing else.
func EnvelopeOps(ops []Operation) []Operation {
	var out []Operation
	for _, op := range ops {
		if op.Kind == OpVentIn || op.Kind == OpRoofIn {
			out = append(out, op)
		}
	}
	return out
}
