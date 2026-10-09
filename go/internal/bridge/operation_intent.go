package bridge

import (
	"context"
	"strings"

	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// MaxOperationIntent caps Action.purpose: one short line for the in-game
// "activity" overlay.
const MaxOperationIntent = 80

type operationIntentKey struct{}

// WithOperationIntent names why the writes under ctx are issued. Actions/Apply
// stamps it as the purpose of every action that carries none, so the
// dispatcher sets it once instead of every action builder.
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

// stampPurpose returns actions with ctx's intent as the purpose of each one
// that has none; the caller's messages are never mutated.
func stampPurpose(ctx context.Context, actions []*o.Action) []*o.Action {
	intent := OperationIntentFrom(ctx)
	if intent == "" {
		return actions
	}
	stamped := make([]*o.Action, len(actions))
	for i, action := range actions {
		stamped[i] = action
		if action.Purpose == nil {
			stamped[i] = proto.Clone(action).(*o.Action)
			stamped[i].Purpose = proto.String(intent)
		}
	}
	return stamped
}
