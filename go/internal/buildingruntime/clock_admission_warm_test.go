package buildingruntime

import (
	"context"
	"errors"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

type admissionWarmNative struct {
	*schedulerNative
	requests []*o.BundleRequest
	paused   bool
	fail     bool
}

func (n *admissionWarmNative) ReadBundle(ctx context.Context, req *o.BundleRequest) (*o.BundleReply, bridge.Result, error) {
	n.requests = append(n.requests, proto.Clone(req).(*o.BundleRequest))
	if n.fail {
		return nil, bridge.Result{}, errors.New("read failed")
	}
	r, raw, err := n.schedulerNative.ReadBundle(ctx, req)
	if r.GetObserved() != nil {
		r.GetObserved().Paused = proto.Bool(n.paused)
	}
	return r, raw, err
}

func TestClockAdmissionWarmReusesOnlyUnchangedPausedScope(t *testing.T) {
	for _, change := range []string{"none", "tick", "generation", "running", "expired", "invalidated", "write", "failed"} {
		t.Run(change, func(t *testing.T) {
			s, f := schedulerFixture(t)
			n := &admissionWarmNative{schedulerNative: f, paused: true, fail: change == "failed"}
			s.native = n
			s.warmAdmission(context.Background(), f.status.Context)
			if n.requests[0].GetEmergency() != true || !n.requests[0].GetColonistPawns() || n.requests[0].GetClockStatus() {
				t.Fatal("warm must read only admission observations", n.requests[0])
			}
			if change != "failed" && s.admissionWarm.Load() == nil {
				t.Fatal("warm missing")
			}
			switch change {
			case "tick":
				f.status.Context.Tick = proto.Int64(f.status.Context.GetTick() + 1)
			case "generation":
				f.status.Context.NativeGeneration = proto.Uint64(f.status.Context.GetNativeGeneration() + 1)
			case "running":
				n.paused = false
			case "expired":
				s.admissionWarm.Load().at = s.clock.Now().Add(-time.Minute)
			case "invalidated":
				s.admissionWarm.Store(nil)
			case "write":
				s.facts.cache.Invalidate()
			case "failed":
				n.fail = false
			}
			reply, err := s.readStepBundle(context.Background(), &o.BundleRequest{ClockStatus: proto.Bool(true), Emergency: proto.Bool(true), ColonistPawns: proto.Bool(true)})
			if err != nil || reply.GetObserved().Emergency == nil {
				t.Fatal(reply, err)
			}
			if change == "none" {
				if len(n.requests) != 2 || n.requests[1].GetEmergency() || n.requests[1].GetColonistPawns() || !n.requests[1].GetClockStatus() {
					t.Fatal("step should read fresh status without warmed families", n.requests)
				}
			} else if !n.requests[len(n.requests)-1].GetEmergency() || !n.requests[len(n.requests)-1].GetColonistPawns() {
				t.Fatal("changed scope must re-read admission families", n.requests)
			}
		})
	}
}

func TestClockPollWarmsAdmissionAfterStop(t *testing.T) {
	s, f, _ := clockPollFixture(t)
	n := &admissionWarmNative{schedulerNative: f, paused: true}
	s.native = n
	page := clockPollPage(f, 0, "benign")
	// A budget stop is benign and can readmit after the page is reviewed.
	page.Events[0].Event = &k.Event_Stopped{Stopped: &k.StopEvent{Reason: k.StopReason_STOP_REASON_TICK_BUDGET.Enum(), Evidence: &k.StopEvent_Budget{Budget: &k.BudgetReached{StartTick: proto.Int64(0), TickDeadline: proto.Int64(f.status.Context.GetTick()), ActualTick: proto.Int64(f.status.Context.GetTick())}}}}
	result, err := s.PollEvents(context.Background(), &clockPollNative{core: f.clockCoreFake, page: page}, 128, 0)
	if err != nil || !result.Stopped || s.admissionWarm.Load() == nil {
		t.Fatal(result, err)
	}
}
