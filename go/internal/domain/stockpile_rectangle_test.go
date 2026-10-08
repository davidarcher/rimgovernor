package domain

func stockpileTestRectangle(cells []Cell) GroundRect {
	if len(cells) == 0 {
		return GroundRect{}
	}
	lo, hi := cells[0], cells[0]
	for _, c := range cells {
		lo.X = min(lo.X, c.X)
		lo.Z = min(lo.Z, c.Z)
		hi.X = max(hi.X, c.X)
		hi.Z = max(hi.Z, c.Z)
	}
	return GroundRect{Origin: lo, Width: hi.X - lo.X + 1, Height: hi.Z - lo.Z + 1}
}
