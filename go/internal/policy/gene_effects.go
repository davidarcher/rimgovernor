package policy

// StatModifier is the combined effect of a pawn's active genes on one stat:
// offsets add, factors multiply (the game's stat pipeline order is base plus
// offsets, then factors).
type StatModifier struct{ Offset, Factor float64 }

// GeneEffects is what a pawn's active genes do, resolved from the catalog's
// typed gene rows (bridge.BiotechCatalog.GeneEffects); policy never matches
// a gene by name. DisabledNeeds and EnabledNeeds are NeedDef names.
type GeneEffects struct {
	Stats                       map[string]StatModifier
	DisabledNeeds, EnabledNeeds map[string]bool
}

// Stat is the genes' combined modifier on a stat; the identity when no
// active gene touches it.
func (g GeneEffects) Stat(stat string) StatModifier {
	if m, ok := g.Stats[stat]; ok {
		return m
	}
	return StatModifier{Factor: 1}
}

// NeedDisabled reports whether an active gene removes the need and no active
// gene gives it back.
func (g GeneEffects) NeedDisabled(need string) bool {
	return g.DisabledNeeds[need] && !g.EnabledNeeds[need]
}

// apply scales a stat whose other offsets sum to offset, returning the
// resulting offset from 1.
func (g GeneEffects) apply(stat string, offset float64) float64 {
	m := g.Stat(stat)
	return (1+offset+m.Offset)*m.Factor - 1
}
