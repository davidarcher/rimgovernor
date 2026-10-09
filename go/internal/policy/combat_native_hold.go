package policy

// stationaryRangedHold delegates only the ordinary firing line. Native
// acquisition cannot honor target exclusions, so spared bleeders and nearby
// exploders retain targeted control. Tactical movement and specialist weapons
// keep their existing execution paths.
func stationaryRangedHold(m CombatMemory, r CombatRole, s CombatPawnState) bool {
	return m.Tactic == TacticHold && m.Flank == nil && len(m.Groups) == 0 && !m.MechLure &&
		r.Ranged && r.Cell != nil && r.Duty == "" && !r.Retreat && r.Home == nil &&
		r.Mortar == nil && r.Ground == nil && r.Repair == nil &&
		s.WeaponFacts.Hunts() && !s.WeaponFacts.EMP && !s.WeaponFacts.OneUse && !s.WeaponFacts.ForcedMiss
}
