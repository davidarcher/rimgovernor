package policy

// switchThreshold is the score a move of built room r must gain (#2865): the
// replan gain plus what building the room twice costs (the walls, floor and
// furniture built again), so a replan never flickers between near-equal
// layouts. The relocation pass of the embedding prices a move with it
// (layout_built.go).
func switchThreshold(r PlannedRoom) int {
	return planWeights.ReplanGain + planWeights.BuiltSwitch + planWeights.BuiltSwitchCell*r.Region.Area()
}
