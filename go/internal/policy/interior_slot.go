package policy

// slotFamilies groups definitions that may take one another's interior
// slot (#819): each family shares one footprint (size and interaction
// offset at rotation North) and one room role, so a slot planned for any
// member stays regular and role-correct when another member takes it.
// Butchery is in no family: its blood filth must never take a kitchen or
// workshop slot (SeparationProtectedCells).
var slotFamilies = map[string]string{
	// Stoves: 3x1, interaction cell in front, kitchen role.
	"FueledStove":   "stove",
	"ElectricStove": "stove",
	// Workshop benches: 3x1, interaction cell in front, workshop role.
	"TableStonecutter":       "bench3x1",
	"FueledSmithy":           "bench3x1",
	"ElectricSmithy":         "bench3x1",
	"HandTailoringBench":     "bench3x1",
	"ElectricTailoringBench": "bench3x1",
	"TableMachining":         "bench3x1",
	"ElectricSmelter":        "bench3x1",
	"TableSculpting":         "bench3x1",
	"Brewery":                "bench3x1",
	"DrugLab":                "bench3x1",
}

// Accepts reports whether a definition may take this slot: the slot's own
// definition or one of its footprint-and-role family.
func (p InteriorPiece) Accepts(def string) bool {
	if def == p.Def {
		return true
	}
	family, ok := slotFamilies[p.Def]
	return ok && slotFamilies[def] == family
}
