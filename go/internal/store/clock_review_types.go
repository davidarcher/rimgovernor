package store

type ClockHoldKind string

const (
	ClockInterruptionHold ClockHoldKind = "interruption"
	ClockGapHold          ClockHoldKind = "gap"
)

// Holds refer only to immutable captured cursor positions. They carry no grant
// and cannot suppress a current unsafe native observation.
type ClockHold struct {
	Kind                      ClockHoldKind
	FromCursor, ThroughCursor int64
}

type ClockReviewState struct {
	Revision                                        uint64
	InboxCursor, ReviewedCursor, AcknowledgedCursor int64
	Holds                                           []ClockHold
}

type ClockAcknowledgement struct {
	RequestID        string
	ExpectedRevision uint64
	ThroughCursor    int64
}
