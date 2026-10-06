package domain

// CorpseOf is a corpse's inner pawn class, as the waste census reports it
// (#832) and a corpse bill's ingredient filter names it (#833).
type CorpseOf string

const (
	// CorpseColonist is a player-faction humanlike.
	CorpseColonist CorpseOf = "colonist"
	// CorpseStranger is any other humanlike: raiders, visitors, prisoners.
	CorpseStranger CorpseOf = "stranger"
	// CorpseAnimal is any non-humanlike.
	CorpseAnimal CorpseOf = "animal"
)

// Valid reports one of the three classes.
func (c CorpseOf) Valid() bool {
	return c == CorpseColonist || c == CorpseStranger || c == CorpseAnimal
}

// RotStage is a thing's rot stage, as the waste census reports it and a
// corpse bill's minimum names it (#1810). Empty is unknown (or, on a bill,
// any stage).
type RotStage string

const (
	RotFresh      RotStage = "fresh"
	RotRotting    RotStage = "rotting"
	RotDessicated RotStage = "dessicated"
)

// Valid reports one of the three stages.
func (r RotStage) Valid() bool {
	return r == RotFresh || r == RotRotting || r == RotDessicated
}

// Spoiled reports a corpse past fresh: rotting or desiccated.
func (r RotStage) Spoiled() bool { return r == RotRotting || r == RotDessicated }
