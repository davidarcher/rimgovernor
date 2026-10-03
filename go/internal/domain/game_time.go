package domain

// The game's calendar (GenDate): the one place Go states it. The definition
// catalog's constants block carries the game's own values and bridge refuses
// a catalog whose values differ, so these never disagree with the game.
const (
	TicksPerHour = 2500
	TicksPerDay  = 60000
	DaysPerYear  = 60
)
