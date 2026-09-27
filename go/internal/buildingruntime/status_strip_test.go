package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	"google.golang.org/protobuf/proto"
)

func TestClockPauseNamesWhoStoppedTheClock(t *testing.T) {
	stopped := func(reason k.StopReason, paused bool) *k.Status {
		return &k.Status{ActualPaused: proto.Bool(paused), State: &k.Status_Stopped{Stopped: &k.Stopped{Reason: reason.Enum()}}}
	}
	for _, c := range []struct {
		status *k.Status
		want   policy.ClockPause
	}{
		{&k.Status{}, policy.ClockPause{}},
		{stopped(k.StopReason_STOP_REASON_TICK_BUDGET, true), policy.ClockPause{}},
		{stopped(k.StopReason_STOP_REASON_EXTERNAL_PAUSE, false), policy.ClockPause{}},
		{stopped(k.StopReason_STOP_REASON_EXTERNAL_PAUSE, true), policy.ClockPause{By: "player", Reason: "external_pause"}},
		{stopped(k.StopReason_STOP_REASON_LETTER_PAUSE, true), policy.ClockPause{By: "letter", Reason: "letter_pause", Held: true}},
		{stopped(k.StopReason_STOP_REASON_HOSTILE, true), policy.ClockPause{By: "hold", Reason: "hostile", Held: true}},
		{stopped(k.StopReason_STOP_REASON_LEASE_EXPIRED, true), policy.ClockPause{By: "governor", Reason: "lease_expired", Held: true}},
	} {
		if got := clockPause(c.status); got != c.want {
			t.Fatalf("%v = %+v, want %+v", c.status, got, c.want)
		}
	}
}
