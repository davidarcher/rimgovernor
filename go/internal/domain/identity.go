// Package domain owns typed intent and progress, independent of adapters.
package domain

import (
	"crypto/rand"
	"errors"
	"fmt"
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

// SnapshotFact and SetSnapshotFact are the colony-snapshot codec's hooks
// (internal/snapshot). Fact has no JSON form of its own: journal payloads
// are canonical, and a Fact field in them must keep encoding as it does.
func (f Fact[T]) SnapshotFact() (any, bool) { return f.value, f.known }

// SetSnapshotFact makes f known with the value decode fills.
func (f *Fact[T]) SetSnapshotFact(decode func(any) error) error {
	var v T
	if err := decode(&v); err != nil {
		return err
	}
	*f = Known(v)
	return nil
}

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

// SameWorld reports whether both snapshots name one loaded map, whatever
// plan, revision or native generation each carries.
func (s GenerationSnapshot) SameWorld(other GenerationSnapshot) bool { return s.sameWorld(other) }
func (s GenerationSnapshot) sameWorld(other GenerationSnapshot) bool {
	return s.Colony == other.Colony && s.Map == other.Map && s.Load == other.Load
}

// sameColonyMap is the goal world check: goals belong to the save, so
// a load change keeps them.
func (s GenerationSnapshot) sameColonyMap(other GenerationSnapshot) bool {
	return s.Colony == other.Colony && s.Map == other.Map
}
func validID(s string) bool {
	return utf8.ValidString(s) && strings.TrimSpace(s) != "" && len(s) <= 256 && !strings.ContainsRune(s, '\x00')
}

// MintPlanID mints a fresh plan id at admission: a bare random
// version-4 UUID. Nothing re-derives it; a plan is found again by
// its stored (goal, epoch, method) key.
func MintPlanID() PlanID {
	var b [16]byte
	if _, err := rand.Read(b[:]); err != nil {
		panic(err)
	}
	b[6] = b[6]&0x0f | 0x40
	b[8] = b[8]&0x3f | 0x80
	return PlanID(fmt.Sprintf("%x-%x-%x-%x-%x", b[0:4], b[4:6], b[6:8], b[8:10], b[10:16]))
}
