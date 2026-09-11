package store

import (
	"bytes"
	"encoding/json"
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
)

type ClockIntent struct {
	RequestID string
	Snapshot  domain.GenerationSnapshot
	Command   bridge.ClockCommand
	Window    *ClockWindowAdmission
}
type ClockPhase string

const (
	ClockPrepared   ClockPhase = "prepared"
	ClockDispatched ClockPhase = "dispatched"
	ClockUncertain  ClockPhase = "uncertain"
	ClockApplied    ClockPhase = "applied"
	ClockRefused    ClockPhase = "refused"
)

type ClockAttempt struct {
	Intent        ClockIntent
	NativeAttempt *c.AttemptKey
	Phase         ClockPhase
	Reply         *k.ControlReply
	// SupersededAt retires only the original world scope; Phase retains uncertainty.
	SupersededAt *c.ObservationContext
}

const clockRecordLimit = 1 << 20

// The closed discriminator carries only lease-free intent. Nested native values
// use official deterministic binary encoding, retaining optional field presence.
type clockIntentRecord struct {
	Snapshot          domain.GenerationSnapshot
	Kind              string
	Speed             int32
	LeaseMS, MaxTicks uint32
	Policy, Original  []byte
}

func clockExpectation(v ClockAttempt) bridge.ClockExpectation {
	s := v.Intent.Snapshot
	return bridge.ClockExpectation{Identity: &c.Identity{ColonyId: proto.String(string(s.Colony)), MapId: proto.Int32(int32(s.Map)), LoadToken: proto.String(string(s.Load))}, Attempt: v.NativeAttempt, Owner: &a.Owner{ControllerSessionId: proto.String(v.NativeAttempt.GetControllerSessionId()), PlayerDirection: proto.Uint64(uint64(s.Direction))}, NativeGeneration: uint64(s.Native), Command: v.Intent.Command}
}
func validateClockIntent(v ClockAttempt) error {
	s := v.Intent.Snapshot
	if err := submissionID(v.Intent.RequestID); err != nil {
		return err
	}
	if s.Validate() != nil || s.Direction == 0 || s.Native == 0 || s.Revision == 0 {
		return errors.New("invalid clock admission snapshot")
	}
	if v.NativeAttempt == nil || v.NativeAttempt.GetAttemptId() != 1 {
		return errors.New("clock command requires its own first attempt")
	}
	return bridge.ValidateClockExpectation(clockExpectation(v))
}
func clockBinary(m proto.Message) ([]byte, error) {
	b, err := (proto.MarshalOptions{Deterministic: true}).Marshal(m)
	if err != nil {
		return nil, err
	}
	if len(b) > clockRecordLimit {
		return nil, errors.New("clock evidence exceeds bound")
	}
	return b, nil
}
func clockUnmarshal(b []byte, m proto.Message) error {
	if len(b) > clockRecordLimit {
		return errors.New("clock evidence exceeds bound")
	}
	if err := (proto.UnmarshalOptions{RecursionLimit: 64}).Unmarshal(b, m); err != nil {
		return err
	}
	canonical, err := clockBinary(m)
	if err != nil {
		return err
	}
	if !bytes.Equal(b, canonical) {
		return errors.New("noncanonical clock protobuf")
	}
	return nil
}
func encodeClockIntent(v ClockAttempt) ([]byte, error) {
	if err := validateClockIntent(v); err != nil {
		return nil, err
	}
	record := clockIntentRecord{Snapshot: v.Intent.Snapshot}
	var err error
	switch command := v.Intent.Command; {
	case command.Start != nil:
		record.Kind = "start"
		record.Speed = int32(command.Start.Speed)
		record.LeaseMS = command.Start.LeaseMS
		record.MaxTicks = command.Start.MaxTicks
		record.Policy, err = clockBinary(command.Start.Policy)
	case command.Renew != nil:
		record.Kind = "renew"
		record.LeaseMS = command.Renew.LeaseMS
		record.Original, err = clockBinary(command.Renew.Original)
	case command.Speed != nil:
		record.Kind = "speed"
		record.Speed = int32(command.Speed.Speed)
		record.Original, err = clockBinary(command.Speed.Original)
	}
	if err != nil {
		return nil, err
	}
	b, err := json.Marshal(record)
	if len(b) > clockRecordLimit {
		return nil, errors.New("clock intent exceeds bound")
	}
	return b, err
}
func decodeClockIntent(id string, attempt *c.AttemptKey, b []byte) (ClockIntent, error) {
	if len(b) > clockRecordLimit {
		return ClockIntent{}, errors.New("clock intent exceeds bound")
	}
	var record clockIntentRecord
	decoder := json.NewDecoder(bytes.NewReader(b))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(&record); err != nil {
		return ClockIntent{}, err
	}
	canonical, err := json.Marshal(record)
	if err != nil || !bytes.Equal(b, canonical) {
		return ClockIntent{}, errors.New("noncanonical clock intent")
	}
	intent := ClockIntent{RequestID: id, Snapshot: record.Snapshot}
	switch record.Kind {
	case "start":
		policy := &k.WatchPolicy{}
		if err = clockUnmarshal(record.Policy, policy); err != nil {
			return ClockIntent{}, err
		}
		intent.Command.Start = &bridge.ClockStart{Speed: k.Speed(record.Speed), Policy: policy, LeaseMS: record.LeaseMS, MaxTicks: record.MaxTicks}
	case "renew", "speed":
		epoch := &k.Epoch{}
		if err = clockUnmarshal(record.Original, epoch); err != nil {
			return ClockIntent{}, err
		}
		if record.Kind == "renew" {
			intent.Command.Renew = &bridge.ClockRenew{Original: epoch, LeaseMS: record.LeaseMS}
		} else {
			intent.Command.Speed = &bridge.ClockSpeed{Original: epoch, Speed: k.Speed(record.Speed)}
		}
	default:
		return ClockIntent{}, errors.New("unknown persisted clock command")
	}
	encoded, err := encodeClockIntent(ClockAttempt{Intent: intent, NativeAttempt: attempt})
	if err != nil {
		return ClockIntent{}, err
	}
	if !bytes.Equal(encoded, b) {
		return ClockIntent{}, errors.New("extraneous clock command fields")
	}
	return intent, nil
}
func clockReplyPhase(reply *k.ControlReply) ClockPhase {
	switch v := reply.Outcome.(type) {
	case *k.ControlReply_Receipt:
		if v.Receipt.GetApplied() != nil {
			return ClockApplied
		}
		return ClockUncertain
	case *k.ControlReply_Failure:
		if v.Failure.GetCode() == c.FailureCode_FAILURE_CODE_ATTEMPT_CONFLICT {
			return ClockUncertain
		}
		return ClockRefused
	default:
		return ClockRefused
	}
}
