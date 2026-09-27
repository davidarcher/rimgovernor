package bridge

import (
	"context"
	"strings"

	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// MaxOperationIntent caps Operation.intent: one short line for the in-game
// inspector (#822).
const MaxOperationIntent = 80

type operationIntentKey struct{}

// WithOperationIntent names why the writes under ctx are issued. protoCall
// stamps it on every operations_execute request whose operation carries no
// intent of its own, so the dispatcher sets it once instead of every
// operation builder (#822).
func WithOperationIntent(ctx context.Context, intent string) context.Context {
	if intent == "" {
		return ctx
	}
	return context.WithValue(ctx, operationIntentKey{}, intent)
}

// OperationIntentFrom is the intent WithOperationIntent set on ctx.
func OperationIntentFrom(ctx context.Context) string {
	v, _ := ctx.Value(operationIntentKey{}).(string)
	return v
}

// FormatOperationIntent renders "<label>: <reason>" as printable ASCII,
// whitespace collapsed and capped at MaxOperationIntent. Either part may be
// empty; both empty is no intent.
func FormatOperationIntent(label, reason string) string {
	label, reason = asciiLine(label), asciiLine(reason)
	out := label
	if label != "" && reason != "" {
		out = label + ": " + reason
	} else if reason != "" {
		out = reason
	}
	if len(out) > MaxOperationIntent {
		out = strings.TrimRight(out[:MaxOperationIntent], " :")
	}
	return out
}

func asciiLine(s string) string {
	var b strings.Builder
	for _, r := range s {
		switch {
		case r == '\t' || r == '\n' || r == '\r':
			b.WriteByte(' ')
		case r >= 0x20 && r < 0x7f:
			b.WriteRune(r)
		}
	}
	return strings.Join(strings.Fields(b.String()), " ")
}

// stampOperationIntent returns request with ctx's intent on its operation
// when that operation has none; the caller's message is never mutated.
func stampOperationIntent(ctx context.Context, name string, request proto.Message) proto.Message {
	execute, ok := request.(*o.ExecuteRequest)
	if name != "rimgovernor/operations_execute" || !ok || execute.GetOperation() == nil || execute.GetOperation().Intent != nil {
		return request
	}
	intent := OperationIntentFrom(ctx)
	if intent == "" {
		return request
	}
	stamped := proto.Clone(execute).(*o.ExecuteRequest)
	stamped.Operation.Intent = proto.String(intent)
	return stamped
}
