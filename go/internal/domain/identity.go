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
