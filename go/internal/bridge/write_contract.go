package bridge

import (
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/encoding/protowire"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
)

// Shared checks the native write and read contracts validate with.
func buildingAttempt(attempt *c.AttemptKey) error {
	if attempt == nil || attempt.ControllerSessionId == nil || attempt.ActionId == nil || attempt.AttemptId == nil || attempt.GetAttemptId() == 0 || validID(attempt.GetControllerSessionId()) != nil || validID(attempt.GetActionId()) != nil {
		return contract("building attempt presence")
	}
	return buildingUnknown(attempt)
}
func buildingContext(value *c.ObservationContext, identity *c.Identity, generation uint64, exact bool) error {
	if err := ValidateContext(value); err != nil {
		return err
	}
	if !sameIdentity(value.Identity, identity) || (exact && (value.NativeGeneration == nil || value.GetNativeGeneration() != generation)) {
		return contract("building context mismatch")
	}
	return nil
}
func buildingUnknown(message proto.Message) error {
	if message == nil {
		return contract("missing building message")
	}
	var visit func(protoreflect.Message, int) error
	visit = func(m protoreflect.Message, depth int) error {
		if depth > 64 {
			return contract("excessive depth in %s", m.Descriptor().FullName())
		}
		if raw := m.GetUnknown(); len(raw) != 0 {
			return contract("unknown fields %v in %s", unknownNumbers(raw), m.Descriptor().FullName())
		}
		var err error
		m.Range(func(f protoreflect.FieldDescriptor, v protoreflect.Value) bool {
			if f.Message() == nil {
				return true
			}
			if f.IsList() {
				list := v.List()
				for i := 0; i < list.Len(); i++ {
					if err = visit(list.Get(i).Message(), depth+1); err != nil {
						return false
					}
				}
			} else if f.IsMap() {
				// f.Message() is the map entry; a scalar-valued map
				// (doctor_chances) has nothing to visit.
				if f.MapValue().Message() == nil {
					return true
				}
				v.Map().Range(func(_ protoreflect.MapKey, v protoreflect.Value) bool {
					err = visit(v.Message(), depth+1)
					return err == nil
				})
			} else {
				err = visit(v.Message(), depth+1)
			}
			return err == nil
		})
		return err
	}
	return visit(message.ProtoReflect(), 0)
}

// unknownNumbers lists the field numbers in raw unknown bytes, so a contract
// failure names the field native sent that this build does not know.
func unknownNumbers(raw protoreflect.RawFields) []protowire.Number {
	var out []protowire.Number
	for len(raw) > 0 {
		number, _, n := protowire.ConsumeField(raw)
		if n < 0 {
			break
		}
		out = append(out, number)
		raw = raw[n:]
	}
	return out
}
