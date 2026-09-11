package store

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// ClockWindowAdmission binds a finite start to the reviewed profile evidence.
// It grants no authority and is rechecked atomically at durable dispatch.
type ClockWindowAdmission struct {
	Profile        string
	Snapshot       domain.GenerationSnapshot
	Tick           domain.Tick
	ReviewRevision uint64
	CapturedCursor int64
	MaxTicks       uint32
}
