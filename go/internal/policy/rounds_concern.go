package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// ConcernID identifies a maintained need independently of its executable plans.
type ConcernID = domain.ConcernID

// RoundsConcern is an inspection's observed deficit and method availability.
// It does not reserve a worker; RimWorld schedules pawn work.
type RoundsConcern struct {
	ID                ConcernID
	Priority          int
	Deficit           domain.Fact[float64]
	MethodUnavailable bool
}
