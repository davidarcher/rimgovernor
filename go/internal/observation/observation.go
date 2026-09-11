// Package observation maps native wire observations into explicit domain facts.
// It does not infer player authority or manufacture runtime generations.
package observation

import (
	"context"
	"errors"
	"fmt"
	"strings"
	"time"
	"unicode/utf8"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

var (
	ErrContract = errors.New("invalid native observation")
	ErrChanged  = errors.New("native observation context changed")
	ErrStale    = errors.New("native observation is stale")
)

type Identity struct {
	Colony                       domain.ColonyID
	Map                          domain.MapID
	Load                         domain.LoadID
	Tick                         domain.Tick
	ObservationBatchVersion      int32
	PlacementPreviewBatchVersion int32
}

func (i Identity) Validate() error {
	for _, value := range []string{string(i.Colony), string(i.Load)} {
		if !utf8.ValidString(value) || strings.TrimSpace(value) == "" || len(value) > 256 {
			return fmt.Errorf("%w: colony/load identity", ErrContract)
		}
	}
	if i.Map < 0 || i.Tick < 0 || i.ObservationBatchVersion < 0 || i.PlacementPreviewBatchVersion < 0 {
		return fmt.Errorf("%w: negative native identity field", ErrContract)
	}
	return nil
}
func (i Identity) SameContext(other Identity) bool {
	return i.Colony == other.Colony && i.Map == other.Map && i.Load == other.Load
}

type Availability string

const (
	GameLoaded Availability = "game_loaded"
	NoMap      Availability = "no_map"
	NoGame     Availability = "no_game"
)

type Speed string

const (
	Paused    Speed = "Paused"
	Normal    Speed = "Normal"
	Fast      Speed = "Fast"
	Superfast Speed = "Superfast"
	Ultrafast Speed = "Ultrafast"
)

type Status struct {
	Availability Availability
	Paused       domain.Fact[bool]
	ForcePaused  domain.Fact[bool]
	Speed        domain.Fact[Speed]
}

// Snapshot brackets the status read with identity reads. Equal ticks describe
// the observed interval; they do not turn separate native calls into an atomic read.
// No native generation, manual authority or player direction is inferred here.
type Snapshot struct {
	Before     Identity
	After      Identity
	Status     Status
	StartedAt  time.Time
	ObservedAt time.Time
}

func (s Snapshot) SameTick() bool { return s.Before.Tick == s.After.Tick }
func (s Snapshot) CheckFresh(now time.Time, maxAge time.Duration, current Identity) error {
	if err := s.Before.Validate(); err != nil {
		return err
	}
	if err := s.After.Validate(); err != nil {
		return err
	}
	if err := current.Validate(); err != nil {
		return err
	}
	if !s.Before.SameContext(s.After) || !s.After.SameContext(current) || s.After.Tick < s.Before.Tick || current.Tick < s.After.Tick {
		return ErrChanged
	}
	if maxAge < 0 || s.StartedAt.IsZero() || s.ObservedAt.IsZero() || s.ObservedAt.Before(s.StartedAt) || now.Before(s.StartedAt) || now.Before(s.ObservedAt) || now.Sub(s.StartedAt) > maxAge {
		return ErrStale
	}
	if s.Status.Availability != GameLoaded {
		return ErrChanged
	}
	return nil
}

// Source is implemented by bridge.Client. Typed observation code never receives
// arbitrary call names or arguments through this interface.
type Source interface {
	Identity(context.Context) (bridge.Result, error)
	Status(context.Context) (bridge.Result, error)
}
type Clock interface{ Now() time.Time }

type Reading struct {
	Snapshot Snapshot
	Receipts [3]bridge.Result
}

func Observe(ctx context.Context, source Source, clock Clock) (Reading, error) {
	var result Reading
	if source == nil || clock == nil {
		return result, fmt.Errorf("%w: missing observation dependency", ErrContract)
	}
	result.Snapshot.StartedAt = clock.Now()
	var err error
	result.Receipts[0], err = source.Identity(ctx)
	if err != nil {
		return result, err
	}
	result.Snapshot.Before, err = DecodeIdentity(result.Receipts[0].Structured)
	if err != nil {
		return result, err
	}
	result.Receipts[1], err = source.Status(ctx)
	if err != nil {
		return result, err
	}
	result.Snapshot.Status, err = DecodeStatus(result.Receipts[1].Structured)
	if err != nil {
		return result, err
	}
	result.Receipts[2], err = source.Identity(ctx)
	if err != nil {
		return result, err
	}
	result.Snapshot.After, err = DecodeIdentity(result.Receipts[2].Structured)
	if err != nil {
		return result, err
	}
	result.Snapshot.ObservedAt = clock.Now()
	if !result.Snapshot.Before.SameContext(result.Snapshot.After) || result.Snapshot.After.Tick < result.Snapshot.Before.Tick || result.Snapshot.Status.Availability != GameLoaded {
		return result, ErrChanged
	}
	if result.Snapshot.StartedAt.IsZero() || result.Snapshot.ObservedAt.Before(result.Snapshot.StartedAt) {
		return result, ErrStale
	}
	return result, nil
}
