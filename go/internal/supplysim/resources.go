package supplysim

// Resource goods, named as the policy resource definitions.
const (
	Wood        Good = "WoodLog"
	StoneChunks Good = "StoneChunks"
	StoneBlocks Good = "StoneBlocks"
	Steel       Good = "Steel"
	Plasteel    Good = "Plasteel"
	Components  Good = "ComponentIndustrial"
	Cotton      Good = "Cotton"
	Leather     Good = "Leather"
)

// Labor priors in pawn ticks per unit yielded, copied from
// policy.AcquisitionLaborPerUnit (policy/acquisition_catalog.go). Trade is 0.
const (
	ChopWork      = 15.0
	MineWork      = 20.0
	SalvageWork   = 25.0
	LootWork      = 5.0
	ProduceWork   = 60.0 // per unit of product
	DeepDrillWork = 100.0
)

// Floors copied from policy.RoundsPolicy defaults (policy/rounds.go:155) and
// DefaultResourceTargets (policy/stone_blocks.go): the wood latch and the
// steel and component floors. Use them in fixtures as Floor values.
const (
	WoodMin, WoodTarget, WoodMax = 120.0, 350.0, 500.0
	SteelFloor                   = 200.0
	ComponentFloor               = 10.0
)

// Modelling priors. The repo observes a fresh census each tick and models none
// of these dynamics, so each is a stated assumption: tests assert direction and
// ordering, never these values.
const (
	// TreeRegrowFraction is the share of a cluster's maximum wood regrown per
	// day (about a mature tree per 200 trees-days: slow, so over-chopping
	// shows).
	TreeRegrowFraction = 0.005
	// StoneBlocksPerChunk is the Core stonecutting recipe yield.
	StoneBlocksPerChunk = 20.0
)

func labored(s Source, perUnit float64) Source {
	s.Labor = s.Capacity * perUnit
	return s
}

// NewTrees is a tree cluster holding wood (up to max) chopped at perDay and
// regrowing TreeRegrowFraction x max per day.
func NewTrees(id string, wood, max, perDay float64) Source {
	return labored(Source{ID: id, Yields: []Yield{{Wood, 1}}, Capacity: perDay, Finite: true,
		Stock: wood, Max: max, Regen: TreeRegrowFraction * max}, ChopWork)
}

// NewQuarry is a finite stone deposit mined as chunks at perDay.
func NewQuarry(id string, chunks, perDay float64) Source {
	return labored(Source{ID: id, Yields: []Yield{{StoneChunks, 1}}, Capacity: perDay, Finite: true,
		Stock: chunks, Max: chunks}, MineWork)
}

// NewVein is a finite ore vein of good mined at perDay. A buried vein sets
// tunnelDays: the lead between opening and the first delivery.
func NewVein(id string, good Good, units, perDay float64, tunnelDays int) Source {
	s := labored(Source{ID: id, Yields: []Yield{{good, 1}}, Capacity: perDay, Finite: true,
		Stock: units, Max: units, Lead: tunnelDays}, MineWork)
	return s
}

// NewDeepDrill is a drill over a lump of good: perDay while powered with
// powerDraw from World.Power, depleting the lump.
func NewDeepDrill(id string, good Good, lump, perDay, powerDraw float64) Source {
	s := labored(Source{ID: id, Yields: []Yield{{good, 1}}, Capacity: perDay, Finite: true,
		Stock: lump, Max: lump}, DeepDrillWork)
	s.PowerDraw = powerDraw
	return s
}

// NewRecipe is a bench bill turning in per unit of output... batches per day,
// each consuming inPerBatch of input and yielding outPerBatch of output, with
// ProduceWork ticks per output unit.
func NewRecipe(id string, in Good, inPerBatch float64, out Good, outPerBatch, batchesPerDay float64) Source {
	return Source{ID: id, Yields: []Yield{{out, outPerBatch}}, Costs: []Yield{{in, inPerBatch}},
		Capacity: batchesPerDay, Labor: batchesPerDay * outPerBatch * ProduceWork}
}

// NewStonecutter cuts chunks into blocks at the Core recipe ratio.
func NewStonecutter(id string, chunksPerDay float64) Source {
	return NewRecipe(id, StoneChunks, 1, StoneBlocks, StoneBlocksPerChunk, chunksPerDay)
}

// NewLoot is a one-shot windfall of good collected at perDay, only while no
// threat is active.
func NewLoot(id string, good Good, amount, perDay float64) Source {
	return labored(Source{ID: id, Yields: []Yield{{good, 1}}, Capacity: perDay, Finite: true,
		Stock: amount, Max: amount, Safe: true}, LootWork)
}

// NewSalvage is a one-shot wreck of good stripped at perDay under the same
// threat gate as loot.
func NewSalvage(id string, good Good, amount, perDay float64) Source {
	s := NewLoot(id, good, amount, perDay)
	s.Labor = perDay * SalvageWork
	return s
}

// WithYield adds a co-yield per unit, such as leather from a hunt.
func (s Source) WithYield(g Good, perUnit float64) Source {
	s.Yields = append(append([]Yield(nil), s.Yields...), Yield{g, perUnit})
	return s
}
