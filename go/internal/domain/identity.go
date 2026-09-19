// Package domain owns typed intent and progress, independent of adapters.
package domain

import (
	"errors"
	"strings"
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

// FreshFor reports whether an observation at t still describes anchor, the
// tick a step's facts are bound to: never earlier than the anchor (a tick
// rewind is another world) and past it by no more than
// PlanningTickTolerance.
func (t Tick) FreshFor(anchor Tick) bool {
	return t >= anchor && t-anchor <= PlanningTickTolerance
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
