package bridge

import (
	"context"
	"encoding/json"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
	"testing"
)

func TestCreationSelectsRecordedDesignOnce(t *testing.T) {
	wire := recordedCatalog(t)
	catalog, err := DecodeCreationCatalog(&o.CreationDefinitionCatalog{Defs: wire.Defs, ClassChains: wire.ClassChains})
	if err != nil {
		t.Fatal(err)
	}
	choice, err := catalog.StartingIdeoligion("Crashlanded")
	if err != nil {
		t.Fatal(err)
	}
	request := pbNewColonyRequest()
	request.Spec.GovernorIdeoligion = proto.Bool(true)
	n, s := newColonyClient(t, nil)
	reads, writes := 0, 0
	s.handler = func(_ context.Context, argument nativeArgument) (*callResult, error) {
		if argument.Tool == methodDefinitionCatalog {
			reads++
			return pbResult(&o.DefinitionCatalogReply{Outcome: &o.DefinitionCatalogReply_Creation{Creation: &o.CreationDefinitionCatalog{Defs: wire.Defs, ClassChains: wire.ClassChains}}}), nil
		}
		if argument.Tool != "rimgovernor/lifecycle_new_colony" {
			t.Fatalf("unexpected call %s", argument.Tool)
		}
		writes++
		var args struct {
			Request string `json:"request"`
		}
		if err := json.Unmarshal(argument.Arguments, &args); err != nil {
			t.Fatal(err)
		}
		var got l.NewColonyRequest
		if err := protojson.Unmarshal([]byte(args.Request), &got); err != nil {
			t.Fatal(err)
		}
		if !proto.Equal(got.Spec.Ideoligion, WireIdeoligionDesign(choice.Design)) || got.Spec.GovernorIdeoligion != nil {
			t.Fatalf("selected intent differs: %v", &got)
		}
		return pbResult(pbNewColonyCompleted(request)), nil
	}
	if _, _, err := n.NewColony(context.Background(), request); err != nil {
		t.Fatal(err)
	}
	if reads != 1 || writes != 1 || request.Spec.Ideoligion != nil {
		t.Fatalf("dispatches %d/%d or mutated caller", reads, writes)
	}
}

func TestCreationPreservesExplicitDesignAndNoRandomFallback(t *testing.T) {
	request := pbNewColonyRequest()
	request.Spec.Ideoligion = &c.IdeoligionDesign{Memes: []string{"Structure", "Normal"}, Precepts: []string{"Precept"}, Fluid: proto.Bool(true)}
	request.Spec.GovernorIdeoligion = proto.Bool(true)
	n, s := newColonyClient(t, pbNewColonyCompleted(request))
	s.handler = func(_ context.Context, argument nativeArgument) (*callResult, error) {
		if argument.Tool != "rimgovernor/lifecycle_new_colony" {
			t.Fatalf("explicit design unexpectedly selected: %s", argument.Tool)
		}
		var args struct {
			Request string `json:"request"`
		}
		if err := json.Unmarshal(argument.Arguments, &args); err != nil {
			t.Fatal(err)
		}
		var got l.NewColonyRequest
		if err := protojson.Unmarshal([]byte(args.Request), &got); err != nil {
			t.Fatal(err)
		}
		if !proto.Equal(got.Spec.Ideoligion, request.Spec.Ideoligion) || got.Spec.GovernorIdeoligion != nil {
			t.Fatalf("design overwritten: %v", &got)
		}
		return pbResult(pbNewColonyCompleted(request)), nil
	}
	if _, _, err := n.NewColony(context.Background(), request); err != nil {
		t.Fatal(err)
	}
	if len(s.calls) != 1 || request.Spec.GovernorIdeoligion == nil {
		t.Fatal("wrong dispatch count or mutated request")
	}
	request.Spec.Ideoligion = nil
	n, s = newColonyClient(t, nil)
	s.handler = func(_ context.Context, argument nativeArgument) (*callResult, error) {
		if argument.Tool != "rimgovernor/observations_read_definition_catalog" {
			t.Fatal("dispatched without known design")
		}
		return pbResult(&o.DefinitionCatalogReply{Outcome: &o.DefinitionCatalogReply_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_READ_FAILED.Enum(), Detail: proto.String("unread")}}}), nil
	}
	if _, _, err := n.NewColony(context.Background(), request); err == nil {
		t.Fatal("missing facts randomly defaulted")
	}
	if len(s.calls) != 1 {
		t.Fatal("retried unavailable catalog")
	}
}
