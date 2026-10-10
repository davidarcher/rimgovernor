package domain

// The game's calendar (GenDate): the one place Go states it, because policy
// constants are compile-time. The catalog's GameConstants carry the game's own
// values and bridge refuses a catalog whose calendar differs, so these never
// disagree with the game.
const (
	TicksPerHour = 2500
	TicksPerDay  = 60000
	DaysPerYear  = 60

	// TicksPerYear is GenDate.TicksPerYear (a year is DaysPerYear days).
	TicksPerYear = TicksPerDay * DaysPerYear
)
