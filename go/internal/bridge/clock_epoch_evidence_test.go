package bridge

import (
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
	"testing"
)

func TestAssessClockEpochStates(t *testing.T) {
	for _, tc := range []struct {
		name string
		edit func(*k.Status)
		want ClockEpochState
	}{
		{"running", func(*k.Status) {}, ClockEpochRequired},
		{"stopping", func(s *k.Status) {
			s.State = &k.Status_Stopping{Stopping: &k.Stopping{Epoch: clockTestEpoch(), PendingReason: k.StopReason_STOP_REASON_REQUESTED_PAUSE.Enum(), ActualPaused: proto.Bool(true)}}
		}, ClockEpochUncertain},
		{"verified stop", func(s *k.Status) { proto.Reset(s); proto.Merge(s, clockTestStopped()) }, ClockEpochPaused},
		{"external pause", func(s *k.Status) {
			proto.Reset(s)
			proto.Merge(s, clockTestStopped())
			s.GetStopped().PauseRequested = proto.Bool(false)
		}, ClockEpochPaused},
		{"unverified", func(s *k.Status) {
			proto.Reset(s)
			proto.Merge(s, clockTestStopped())
			s.GetStopped().PauseVerified = proto.Bool(false)
		}, ClockEpochRetired},
		{"actual running", func(s *k.Status) {
			proto.Reset(s)
			proto.Merge(s, clockTestStopped())
			s.ActualPaused = proto.Bool(false)
			s.GetStopped().Reason = k.StopReason_STOP_REASON_TICK_BUDGET.Enum()
		}, ClockEpochRetired},
		{"inner running", func(s *k.Status) {
			proto.Reset(s)
			proto.Merge(s, clockTestStopped())
			s.GetStopped().ActualPaused = proto.Bool(false)
		}, ClockEpochRetired},
		{"never started", func(s *k.Status) { s.State = &k.Status_NeverStarted{NeverStarted: &k.NeverStarted{}} }, ClockEpochUncertain},
		{"replacement epoch", func(s *k.Status) { s.GetRunning().Epoch.Owner.Epoch = proto.Int64(2) }, ClockEpochSuperseded},
		{"replacement owner", func(s *k.Status) { s.GetRunning().Epoch.Owner.ControllerSessionId = proto.String("replacement") }, ClockEpochSuperseded},
		{"changed speed lease tick", func(s *k.Status) {
			e := s.GetRunning().Epoch
			e.RequestedSpeed = k.Speed_SPEED_FAST.Enum()
			e.LeaseRemainingMs = proto.Uint32(1)
			e.LastTick = proto.Int64(13)
			s.Context.Tick = proto.Int64(13)
		}, ClockEpochRequired},
	} {
		t.Run(tc.name, func(t *testing.T) {
			s := clockTestStatus()
			tc.edit(s)
			original := clockTestEpoch()
			before := proto.Clone(original)
			got, err := AssessClockEpoch(original, s.Context, s)
			if err != nil || got != tc.want {
				t.Fatal(got, err)
			}
			if !proto.Equal(original, before) {
				t.Fatal("mutated original")
			}
		})
	}
}
func TestAssessClockEpochReplacementAndInvalidEvidence(t *testing.T) {
	original := clockTestEpoch()
	current := authorityTestContext(7)
	if _, err := AssessClockEpoch(original, current, nil); err == nil {
		t.Fatal("missing same-world status")
	}
	for _, field := range []string{"colony", "load", "map"} {
		changed := proto.Clone(current).(*c.ObservationContext)
		switch field {
		case "colony":
			changed.Identity.ColonyId = proto.String("other")
		case "load":
			changed.Identity.LoadToken = proto.String("other")
		case "map":
			changed.Identity.MapId = proto.Int32(1)
		}
		changed.Tick = proto.Int64(0)
		got, err := AssessClockEpoch(original, changed, nil)
		if err != nil || got != ClockEpochSuperseded {
			t.Fatal(field, got, err)
		}
		if _, err := AssessClockEpoch(original, changed, clockTestStatus()); err == nil {
			t.Fatal("mismatched supplied status")
		}
	}
	for name, edit := range map[string]func(*k.Status){
		"origin":         func(s *k.Status) { s.GetRunning().Epoch.Origin.NativeGeneration = proto.Uint64(8) },
		"policy":         func(s *k.Status) { s.GetRunning().Epoch.Policy.HostileWithin = proto.Float32(30) },
		"deadline":       func(s *k.Status) { s.GetRunning().Epoch.TickDeadline = proto.Int64(120) },
		"current tick":   func(s *k.Status) { s.Context.Tick = proto.Int64(11) },
		"unknown fields": func(s *k.Status) { s.ProtoReflect().SetUnknown([]byte{0x78, 1}) },
	} {
		t.Run(name, func(t *testing.T) {
			s := clockTestStatus()
			edit(s)
			if _, err := AssessClockEpoch(original, s.Context, s); err == nil {
				t.Fatal("invalid evidence accepted")
			}
		})
	}
	missing := proto.Clone(current).(*c.ObservationContext)
	missing.Identity.MapId = nil
	if _, err := AssessClockEpoch(original, missing, nil); err == nil {
		t.Fatal("unknown replacement identity")
	}
	if _, err := AssessClockEpoch(nil, current, clockTestStatus()); err == nil {
		t.Fatal("missing original")
	}
	current.ProtoReflect().SetUnknown([]byte{0x78, 1})
	if _, err := AssessClockEpoch(original, current, nil); err == nil {
		t.Fatal("unknown current fields")
	}
	s := clockTestStatus()
	s.State = &k.Status_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_READ_FAILED.Enum()}}
	if got, err := AssessClockEpoch(original, s.Context, s); err != nil || got != ClockEpochUncertain {
		t.Fatal(got, err)
	}
}
