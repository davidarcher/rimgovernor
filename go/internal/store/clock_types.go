package store

import "github.com/davidarcher/RimGovernor/go/internal/store/clock"

type ClockIntent = clock.Intent
type ClockPhase = clock.Phase

const (
	ClockPrepared   = clock.Prepared
	ClockDispatched = clock.Dispatched
	ClockUncertain  = clock.Uncertain
	ClockApplied    = clock.Applied
	ClockRefused    = clock.Refused
)

type ClockAttempt = clock.Attempt

type ClockInboxState = clock.InboxState
type ClockEventPage = clock.EventPage
type ClockInbox = clock.Inbox

type ClockHistoryCompaction = clock.HistoryCompaction

var ClockRequestID = clock.ClockRequestID
