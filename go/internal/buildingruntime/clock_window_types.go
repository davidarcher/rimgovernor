package buildingruntime

import (
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"time"
)

// ClockWindowRequest carries fresh policy facts through the serialized command.
type ClockWindowRequest struct {
	Intent store.ClockIntent
	Facts  policy.ClockWindowFacts
	MaxAge time.Duration
}
