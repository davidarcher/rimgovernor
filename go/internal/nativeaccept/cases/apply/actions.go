package apply

import (
	"context"
	"encoding/json"
	"fmt"
	"strings"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
)

func init() {
	cases.Register(cases.Case{
		Name: "apply/actions",
		Scope: "Actions/Apply contract (#856): every arm of the Action oneof, sent with bogus ids, reaches a native handler " +
			"and comes back refused, and a resent key replays its first result. A Go test cannot see which arms native handles.",
		Start:  cases.LabStart(),
		Budget: 5 * time.Minute,
		Run:    runActions,
	})
}

func runActions(ctx context.Context, s cases.Session) error {
	h, report := s.Harness(), s.Report()
	if !na.Contains(s.Names(), "rimgovernor/operations_apply") {
		return fmt.Errorf("missing rimgovernor/operations_apply in discovery")
	}
	encoded, err := json.Marshal(s.Identity())
	if err != nil {
		return err
	}
	identity := &c.Identity{}
	if err := protojson.Unmarshal(encoded, identity); err != nil {
		return err
	}
	arms := (&o.Action{}).ProtoReflect().Descriptor().Oneofs().ByName("intent").Fields()
	if arms.Len() == 0 {
		return fmt.Errorf("the Action oneof has no arms")
	}
	for i := 0; i < arms.Len(); i++ {
		arm := arms.Get(i)
		action := &o.Action{Key: proto.String("probe-" + string(arm.Name()))}
		intent := action.ProtoReflect().NewField(arm).Message()
		bogusIDs(intent)
		action.ProtoReflect().Set(arm, protoreflect.ValueOfMessage(intent))
		request, err := protojson.Marshal(&o.ApplyRequest{Identity: identity, Actions: []*o.Action{action}})
		if err != nil {
			return err
		}
		var first map[string]any
		for _, attempt := range []string{"send", "resend"} {
			reply, err := h.Wire(ctx, attempt+"-"+string(arm.Name()), "operations_apply", json.RawMessage(request))
			if err != nil {
				return err
			}
			results := na.AsSlice(reply["results"])
			if len(results) != 1 {
				return fmt.Errorf("%s: expected one result, got %#v", arm.Name(), reply)
			}
			result, _ := na.AsMap(results[0])
			refused, ok := na.AsMap(result["refused"])
			if !ok || na.AsString(result["key"]) != action.GetKey() {
				return fmt.Errorf("%s: expected a refusal under the sent key, got %#v", arm.Name(), result)
			}
			if first == nil {
				first = result
				report["refused_"+string(arm.Name())] = refused
			} else if !na.DeepEqual(first, result) {
				return fmt.Errorf("%s: resent key returned %#v, first was %#v", arm.Name(), result, first)
			}
		}
	}
	return nil
}

// bogusIDs sets every top-level string field named *_id to an id no game
// holds.
func bogusIDs(m protoreflect.Message) {
	fields := m.Descriptor().Fields()
	for i := 0; i < fields.Len(); i++ {
		f := fields.Get(i)
		if f.Kind() == protoreflect.StringKind && !f.IsList() && strings.HasSuffix(string(f.Name()), "_id") {
			m.Set(f, protoreflect.ValueOfString("Thing_ActionsProbe"+string(f.Name())))
		}
	}
}
