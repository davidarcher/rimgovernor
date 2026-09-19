// Package domain owns typed intent and progress, independent of adapters.
package domain

import (
	"errors"
	"strings"
	"sync/atomic"
	"unicode/utf8"
)

type ColonyID string
type MapID int32
type LoadID string
type ActionID string

// AttemptID is monotonic within one action; zero means no dispatch yet.
type AttemptID uint64
type PlanID string
type PlanRevision uint64
type NativeGeneration uint64
type Tick int64

// PlanningTickTolerance is how far the game may have ticked past the tick
// a planning step's facts describe before a later observation (a preview,
// a census, a dependency's readback) no longer belongs to the same plan.
// It is the tightest fact family's tolerance (pawns and the emergency
// census; bridge.FactFamily): a stopped clock never moves, and a running
// one at Fast (180 ticks/s) crosses it in under two seconds.
const PlanningTickTolerance Tick = 250

// liveDrift widens the tolerance while an owned clock window runs (#345):
// the ticks the window's observed pace covers in the wall time a step's
// reads already have (the scheduler's MaxAge). A step under a running
// window reads at several ticks by construction, and at boosted Ultrafast
// one bridge round trip alone advances the game past
// PlanningTickTolerance; the drift keeps such a step's reads one plan
// without a per-speed tolerance at every site. It is zero while the clock
// is stopped, so a paused step keeps the tick-exact bound.
var liveDrift atomic.Int64

// SetLiveDrift sets the widening the scheduler measured for the running
// window; zero (a stopped clock, an unknown pace) restores the bound.
func SetLiveDrift(ticks Tick) {
	if ticks < 0 {
		ticks = 0
	}
	liveDrift.Store(int64(ticks))
}

// LiveDrift is the widening in force.
func LiveDrift() Tick { return Tick(liveDrift.Load()) }

// FreshFor reports whether an observation at t still describes anchor, the
// tick a step's facts are bound to: never earlier than the anchor (a tick
// rewind is another world) and past it by no more than
// PlanningTickTolerance plus the LiveDrift of a running window.
func (t Tick) FreshFor(anchor Tick) bool {
	return t >= anchor && t-anchor <= PlanningTickTolerance+LiveDrift()
}

// Covers reports whether an observation at t describes anchor: at or after
// it, or before it by no more than PlanningTickTolerance. The fact cache
// serves an emergency or pawn row under a step scope that far ahead of it
// (the scope is the step's first native read), so under a running window
// a dispatch's cached emergency read may lawfully predate the inspection's
// first read by that much (#244).
func (t Tick) Covers(anchor Tick) bool {
	return t >= anchor || anchor.FreshFor(t)
}

// Fact's zero value is unknown, including for boolean and numeric observations.
type Fact[T any] struct {
	value T
	known bool
}

func Known[T any](value T) Fact[T] { return Fact[T]{value: value, known: true} }
func Unknown[T any]() Fact[T]      { return Fact[T]{} }
func (f Fact[T]) Value() (T, bool) { return f.value, f.known }

// GenerationSnapshot scopes in-flight work to one loaded world (colony, map,
// load token), the plan revision it serves and the native order generation it
// was issued under. There is one author of orders, so no per-acquisition
// direction counter exists: a reload changes Load, and native order-history
// drift changes Native.
type GenerationSnapshot struct {
	Colony   ColonyID
	Map      MapID
	Load     LoadID
	Plan     PlanID
	Revision PlanRevision
	Native   NativeGeneration
}

func (s GenerationSnapshot) Validate() error {
	if !validID(string(s.Colony)) || !validID(string(s.Load)) || !validID(string(s.Plan)) || s.Map < 0 {
		return errors.New("invalid colony, map, load or plan identity")
	}
	return nil
}
func (s GenerationSnapshot) Matches(other GenerationSnapshot) bool { return s == other }
func (s GenerationSnapshot) sameWorld(other GenerationSnapshot) bool {
	return s.Colony == other.Colony && s.Map == other.Map && s.Load == other.Load
}
func validID(s string) bool {
	return utf8.ValidString(s) && strings.TrimSpace(s) != "" && len(s) <= 256 && !strings.ContainsRune(s, '\x00')
}
