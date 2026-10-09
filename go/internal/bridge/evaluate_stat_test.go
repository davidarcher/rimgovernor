package bridge

import (
	"context"
	"strings"
	"testing"
	"time"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func evaluateStatClient(t *testing.T, reply *o.EvaluateStatReply, seen *string) *Client {
	t.Helper()
	return testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*callResult, error) {
		if arg.Tool != evaluateStatTool {
			t.Fatal(arg.Tool)
		}
		if seen != nil {
			*seen = string(arg.Arguments)
		}
		return pbResult(reply), nil
	}}, time.Second)
}

func TestEvaluateStatReturnsValueBreakdownAndShown(t *testing.T) {
	var seen string
	reply := &o.EvaluateStatReply{Outcome: &o.EvaluateStatReply_Observed{Observed: &o.StatEvaluation{Context: pbContext(), Stat: "MaxHitPoints", Value: 120.5, ExplanationLines: []string{"Base: 100", "Stuff: x1.2"}, Shown: true}}}
	got, _, err := evaluateStatClient(t, reply, &seen).EvaluateStat(context.Background(), pbIdentity(), "MaxHitPoints", StatDefSubject("Wall", "BlocksGranite", proto.Int32(2)))
	if err != nil {
		t.Fatal(err)
	}
	if got.Value != 120.5 || !got.Shown || len(got.ExplanationLines) != 2 {
		t.Fatalf("%+v", got)
	}
	if !strings.Contains(seen, "request") {
		t.Fatalf("request not sent: %s", seen)
	}
}

func TestEvaluateStatFailureIsTyped(t *testing.T) {
	reply := &o.EvaluateStatReply{Outcome: &o.EvaluateStatReply_Failure{Failure: &c.Failure{Code: c.FailureCode_FAILURE_CODE_NOT_FOUND.Enum(), Detail: proto.String("no stat Nope")}}}
	if _, _, err := evaluateStatClient(t, reply, nil).EvaluateStat(context.Background(), pbIdentity(), "Nope", StatPawnSubject("p1")); err == nil {
		t.Fatal("unknown stat accepted")
	}
}

func TestEvaluateStatRejectsBadSubjectsBeforeTheWire(t *testing.T) {
	client := evaluateStatClient(t, nil, nil)
	for _, subject := range []*o.StatSubject{nil, {}, StatDefSubject("", "", nil), StatDefSubject("Wall", "", proto.Int32(7)), StatThingSubject(" ")} {
		if _, _, err := client.EvaluateStat(context.Background(), pbIdentity(), "MaxHitPoints", subject); err == nil {
			t.Fatal("bad subject accepted", subject)
		}
	}
}

func TestEvaluateStatRejectsWrongWorld(t *testing.T) {
	ctxOther := pbContext()
	ctxOther.Identity.LoadToken = proto.String("other")
	reply := &o.EvaluateStatReply{Outcome: &o.EvaluateStatReply_Observed{Observed: &o.StatEvaluation{Context: ctxOther, Stat: "MaxHitPoints"}}}
	if _, _, err := evaluateStatClient(t, reply, nil).EvaluateStat(context.Background(), pbIdentity(), "MaxHitPoints", StatPawnSubject("p1")); err == nil {
		t.Fatal("wrong-world evaluation accepted")
	}
}
