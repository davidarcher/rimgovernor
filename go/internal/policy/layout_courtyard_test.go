package policy

// plusPatch is a non-rectangular rich footprint: a plus 20 cells across,
// centred on (70, 70). The layout/rich-soil case audits the tiler's handling
// of it natively.
func plusPatch(x, z int32) bool {
	return (x >= 60 && x < 80 && z >= 66 && z < 74) || (x >= 66 && x < 74 && z >= 60 && z < 80)
}

func plusSurvey(n int32, dx int32) MapSurvey {
	return zoningSurvey(n, func(x, z int32) SurveyCell {
		if plusPatch(x-dx, z-dx) {
			return SurveyCell{Walkable: true, Fertility: 1.4}
		}
		return SurveyCell{Walkable: true, Fertility: 0.5}
	})
}
