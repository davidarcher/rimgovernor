package buildingruntime

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	"time"
)

type ClockEventNative interface {
	ReadClockEvents(context.Context, *k.EventsRequest) (*k.EventsReply, bridge.Result, error)
}

type ClockPollResult struct {
	Review                store.ClockReviewState
	Captured, Interrupted bool
}

type ClockRenewResult struct {
	Attempt             *store.ClockAttempt
	Renewed, Reconciled bool
}

type ClockWorkerConfig struct {
	PollInterval, RenewInterval, StepInterval, MaxBackoff, CallTimeout time.Duration
	PageLimit                                                          uint32
}
