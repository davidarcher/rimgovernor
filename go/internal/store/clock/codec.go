package clock

import (
	"bytes"
	"encoding/json"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
)

const clockRecordLimit = 1 << 20

// RecordLimit is the maximum encoded size of a clock evidence record.
const RecordLimit = clockRecordLimit

// The closed discriminator carries only lease-free intent. Nested native values
// use official deterministic binary encoding, retaining optional field presence.
type clockIntentRecord struct {
	Key               string
	Snapshot          domain.GenerationSnapshot
	Kind              string
	Speed             int32
	LeaseMS, MaxTicks uint32
	Policy, Original  []byte
	Window            *WindowAdmission
	// Omitted when false so records written before the field stay canonical.
	TestAcceleration bool `json:",omitempty"`
	// Likewise omitted at zero (issue #583).
	BlindTickBudget   uint32 `json:",omitempty"`
	MaxTicksPerSecond uint32 `json:",omitempty"`
	// A speed change that carries a ceiling, even one it cannot omit at
	// zero (the ceiling is 1..60000, so presence is the record).
	CeilingSet bool `json:",omitempty"`
	// Player acceleration (issue #627), omitted when fixed.
	PlayerAccelerated bool   `json:",omitempty"`
	FrameBudgetMS     uint32 `json:",omitempty"`
}

func clockExpectation(v Attempt) bridge.ClockExpectation {
	s := v.Intent.Snapshot
	return bridge.ClockExpectation{Identity: &c.Identity{ColonyId: proto.String(string(s.Colony)), MapId: proto.Int32(int32(s.Map)), LoadToken: proto.String(string(s.Load))}, Attempt: v.NativeAttempt, NativeGeneration: uint64(s.Native), Command: v.Intent.Command}
}
func validateClockIntent(v Attempt) error {
	s := v.Intent.Snapshot
	if v.Intent.Key != "" {
		if err := submissionID(v.Intent.Key); err != nil {
			return err
		}
	}
	if _, err := parseClockRequestID(ControllerSessionID(v.NativeAttempt.GetControllerSessionId()), v.Intent.RequestID); err != nil {
		return err
	}
	if s.Validate() != nil || s.Native == 0 || s.Revision == 0 {
		return errors.New("invalid clock admission snapshot")
	}
	if v.NativeAttempt == nil || v.NativeAttempt.GetAttemptId() != 1 {
		return errors.New("clock command requires its own first attempt")
	}
	if err := validateClockWindow(v.Intent); err != nil {
		return err
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
func encodeClockIntent(v Attempt) ([]byte, error) {
	if err := validateClockIntent(v); err != nil {
		return nil, err
	}
	record := clockIntentRecord{Key: v.Intent.Key, Snapshot: v.Intent.Snapshot, Window: v.Intent.Window}
	var err error
	switch command := v.Intent.Command; {
	case command.Start != nil:
		record.Kind = "start"
		record.Speed = int32(command.Start.Speed)
		record.LeaseMS = command.Start.LeaseMS
		record.MaxTicks = command.Start.MaxTicks
		record.TestAcceleration = command.Start.TestAcceleration
		record.BlindTickBudget = command.Start.BlindTickBudget
		record.MaxTicksPerSecond = command.Start.MaxTicksPerSecond
		record.PlayerAccelerated = command.Start.PlayerAccelerated
		record.FrameBudgetMS = command.Start.FrameBudgetMS
		record.Policy, err = clockBinary(command.Start.Policy)
	case command.Renew != nil:
		record.Kind = "renew"
		record.LeaseMS = command.Renew.LeaseMS
		record.Original, err = clockBinary(command.Renew.Original)
	case command.Speed != nil:
		record.Kind = "speed"
		record.Speed = int32(command.Speed.Speed)
		if command.Speed.MaxTicksPerSecond != nil {
			record.MaxTicksPerSecond = *command.Speed.MaxTicksPerSecond
			record.CeilingSet = true
		}
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
func decodeClockIntent(id string, attempt *c.AttemptKey, b []byte) (Intent, error) {
	if len(b) > clockRecordLimit {
		return Intent{}, errors.New("clock intent exceeds bound")
	}
	var record clockIntentRecord
	decoder := json.NewDecoder(bytes.NewReader(b))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(&record); err != nil {
		return Intent{}, err
	}
	canonical, err := json.Marshal(record)
	if err != nil || !bytes.Equal(b, canonical) {
		return Intent{}, errors.New("noncanonical clock intent")
	}
	intent := Intent{RequestID: id, Key: record.Key, Snapshot: record.Snapshot, Window: record.Window}
	switch record.Kind {
	case "start":
		policy := &k.WatchPolicy{}
		if err = clockUnmarshal(record.Policy, policy); err != nil {
			return Intent{}, err
		}
		intent.Command.Start = &bridge.ClockStart{Speed: k.Speed(record.Speed), Policy: policy, LeaseMS: record.LeaseMS, MaxTicks: record.MaxTicks, TestAcceleration: record.TestAcceleration, BlindTickBudget: record.BlindTickBudget, MaxTicksPerSecond: record.MaxTicksPerSecond, PlayerAccelerated: record.PlayerAccelerated, FrameBudgetMS: record.FrameBudgetMS}
	case "renew", "speed":
		epoch := &k.Epoch{}
		if err = clockUnmarshal(record.Original, epoch); err != nil {
			return Intent{}, err
		}
		if record.Kind == "renew" {
			intent.Command.Renew = &bridge.ClockRenew{Original: epoch, LeaseMS: record.LeaseMS}
		} else {
			intent.Command.Speed = &bridge.ClockSpeed{Original: epoch, Speed: k.Speed(record.Speed)}
			if record.CeilingSet {
				ceiling := record.MaxTicksPerSecond
				intent.Command.Speed.MaxTicksPerSecond = &ceiling
			}
		}
	default:
		return Intent{}, errors.New("unknown persisted clock command")
	}
	encoded, err := encodeClockIntent(Attempt{Intent: intent, NativeAttempt: attempt})
	if err != nil {
		return Intent{}, err
	}
	if !bytes.Equal(encoded, b) {
		return Intent{}, errors.New("extraneous clock command fields")
	}
	return intent, nil
}
func clockReplyPhase(reply *k.ControlReply) Phase {
	switch v := reply.Outcome.(type) {
	case *k.ControlReply_Receipt:
		if v.Receipt.GetApplied() != nil {
			return Applied
		}
		return Uncertain
	case *k.ControlReply_Failure:
		if v.Failure.GetCode() == c.FailureCode_FAILURE_CODE_ATTEMPT_CONFLICT {
			return Uncertain
		}
		return Refused
	default:
		return Refused
	}
}
