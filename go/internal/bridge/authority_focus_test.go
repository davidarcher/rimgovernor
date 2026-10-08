package bridge

import (
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
	"testing"
)

func TestAuthorityFocusRequiresExactTargetAndGeneration(t *testing.T) {
	before := pbIdentity()
	target := proto.Clone(before).(*c.Identity)
	target.MapId = proto.Int32(before.GetMapId() + 1)
	request := &a.ControlRequest{Operation: &a.ControlRequest_FocusMap{FocusMap: &a.FocusMap{Identity: before, Target: target, ExpectedGeneration: proto.Uint64(7)}}}
	context := authorityTestContext(8)
	context.Identity = target
	reply := &a.ControlReply{Outcome: &a.ControlReply_FocusedMap{FocusedMap: &a.FocusedMap{Context: context}}}
	if err := validateAuthorityControl(request, reply); err != nil {
		t.Fatal(err)
	}
	context.NativeGeneration = proto.Uint64(9)
	if err := validateAuthorityControl(request, reply); err == nil {
		t.Fatal("accepted intervening generation")
	}
	context.NativeGeneration = proto.Uint64(8)
	context.Identity = before
	if err := validateAuthorityControl(request, reply); err == nil {
		t.Fatal("accepted old map")
	}
}
