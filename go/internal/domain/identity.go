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
type PlanID string
type DirectionID uint64
type PlanRevision uint64
type NativeGeneration uint64
type Tick int64

// Fact's zero value is unknown, including for boolean and numeric observations.
type Fact[T any] struct {
	value T
	known bool
}

func Known[T any](value T) Fact[T] { return Fact[T]{value: value, known: true} }
func Unknown[T any]() Fact[T]      { return Fact[T]{} }
func (f Fact[T]) Value() (T, bool) { return f.value, f.known }

type GenerationSnapshot struct {
	Colony    ColonyID
	Map       MapID
	Load      LoadID
	Direction DirectionID
	Plan      PlanID
	Revision  PlanRevision
	Native    NativeGeneration
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
	return utf8.ValidString(s) && strings.TrimSpace(s) != "" && len(s) <= 256
}
