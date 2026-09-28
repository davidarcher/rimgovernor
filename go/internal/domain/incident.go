package domain

import (
	"encoding/json"
	"errors"
	"strings"
	"unicode/utf8"
)

// IncidentID names one occurrence of a Response (#1019).
type IncidentID string

// MintIncidentID mints a fresh incident id when an occurrence opens.
func MintIncidentID() IncidentID {
	return IncidentID(MintPlanID("incident"))
}

// Incident is one occurrence of a Response: opened by a review assessment,
// closed when the assessment stops asserting it. The open key is (world,
// Kind, Subject); a re-trigger inside an open occurrence refreshes Priority
// and Snapshot and keeps the Trigger it opened with.
type Incident struct {
	ID       IncidentID
	Kind     GoalID // the Response's GoalID
	Subject  PawnID // the pawn for per-pawn kinds (mood, medical), empty otherwise
	Trigger  string
	Priority int // from the latest assessment
	Snapshot GenerationSnapshot
	Started  Tick
	Closed   bool
	Ended    Tick
	Payload  json.RawMessage `json:",omitempty"`
}

func (i Incident) Validate() error {
	if !validID(string(i.ID)) || !validID(string(i.Kind)) || i.Subject != "" && !validID(string(i.Subject)) {
		return errors.New("invalid incident identity")
	}
	if !utf8.ValidString(i.Trigger) || strings.TrimSpace(i.Trigger) == "" || len(i.Trigger) > 256 {
		return errors.New("invalid incident trigger")
	}
	if i.Priority < 0 || i.Priority > 4 || i.Started < 0 || i.Snapshot.Validate() != nil {
		return errors.New("invalid incident assessment")
	}
	if i.Closed && i.Ended < i.Started || !i.Closed && i.Ended != 0 {
		return errors.New("invalid incident lifetime")
	}
	if len(i.Payload) > 4096 || len(i.Payload) != 0 && !json.Valid(i.Payload) {
		return errors.New("invalid incident payload")
	}
	return nil
}
