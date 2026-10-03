package policy

import "slices"

// Native cover definitions the firing line may build (#868).
const (
	DefenseSandbags  = "Sandbags"
	DefenseEmbrasure = "Embrasure"
	// defenseSandbagStuff is the Sandbags' stuff cost per placement.
	defenseSandbagStuff = 5
)

// DefenseCoverChoice picks the firing line's cover (#868). Real Sandbags
// replace the wooden Barricade stand-in when the definition is available
// and one fabric or leather stock covers every firing position; the stock
// the colony holds most of is used. An available Embrasure is named so the
// layout builds it on a firing line that runs along the perimeter wall.
// The sandbag stuffs are those sharing a stuff category with the Sandbags
// def (ItemFacts.StuffsFor). Anything unknown keeps base.
func DefenseCoverChoice(items ItemFacts, base DefenseDefinitions, stock map[Resource]int64, stockKnown, sandbags, embrasure bool, positions int) DefenseDefinitions {
	out := base
	if embrasure {
		out.Embrasure = DefenseEmbrasure
	}
	if !sandbags || !stockKnown || positions <= 0 {
		return out
	}
	stuffs := items.StuffsFor(DefenseSandbags)
	best, most := "", int64(0)
	for resource, count := range stock {
		name := string(resource)
		if slices.Contains(stuffs, resource) && (count > most || count == most && name < best) {
			best, most = name, count
		}
	}
	if best != "" && most >= int64(positions*defenseSandbagStuff) {
		out.Sandbag, out.SandbagStuff = DefenseSandbags, best
	}
	return out
}
