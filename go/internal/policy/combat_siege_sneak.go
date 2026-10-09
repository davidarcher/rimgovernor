package policy

// SiegeSneak attacks a sleeping camp with everyone.
const SiegeSneak SiegeMode = "sneak"

// campAsleep reports a siege camp at least half asleep: of the
// live camped besiegers, half or more on a LayDown job.
func campAsleep(view CombatView) bool {
	besiegers := liveBesiegers(view)
	camped, asleep := 0, 0
	for _, p := range view.Pawns {
		if besiegers[p.ID] != siegeCampToil {
			continue
		}
		camped++
		if p.Job == "LayDown" {
			asleep++
		}
	}
	return camped > 0 && 2*asleep >= camped
}
