package bridge

import (
	"context"
	"math"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

const evaluateStatTool = "rimgovernor/observations_evaluate_stat"

// StatEvaluation is the game's own evaluation of one stat for one subject: the
// final value, the per-part breakdown lines and whether the game shows the stat
// for the subject. It is the oracle for the Go stat evaluator (epic #2621).
type StatEvaluation struct {
	Value            float64
	ExplanationLines []string
	Shown            bool
}

// StatDefSubject names a definition with optional stuff and quality category.
func StatDefSubject(def, stuff string, quality *int32) *o.StatSubject {
	subject := &o.StatDefSubject{DefName: def, Quality: quality}
	if stuff != "" {
		subject.Stuff = proto.String(stuff)
	}
	return &o.StatSubject{Subject: &o.StatSubject_Definition{Definition: subject}}
}

// StatThingSubject names a thing by its load id.
func StatThingSubject(id string) *o.StatSubject {
	return &o.StatSubject{Subject: &o.StatSubject_ThingId{ThingId: id}}
}

// StatPawnSubject names a pawn by its load id.
func StatPawnSubject(id string) *o.StatSubject {
	return &o.StatSubject{Subject: &o.StatSubject_PawnId{PawnId: id}}
}

// EvaluateStat asks the game to evaluate stat for subject. An unknown stat or
// subject is a typed failure, never a zero value.
func (client *Client) EvaluateStat(ctx context.Context, identity *c.Identity, stat string, subject *o.StatSubject) (*StatEvaluation, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return nil, Result{}, err
	}
	if err := validID(stat); err != nil {
		return nil, Result{}, err
	}
	if err := validStatSubject(subject); err != nil {
		return nil, Result{}, err
	}
	request := &o.EvaluateStatRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.CloneOf(identity)}, Stat: stat, Subject: proto.CloneOf(subject)}
	reply := &o.EvaluateStatReply{}
	raw, err := client.protoRead(ctx, evaluateStatTool, request, reply)
	if err != nil {
		return nil, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.EvaluateStatReply_Failure:
		return nil, raw, failure(v.Failure, raw)
	case *o.EvaluateStatReply_Unavailable:
		return nil, raw, unavailable(v.Unavailable, raw)
	case *o.EvaluateStatReply_Observed:
		got := v.Observed
		if got == nil {
			return nil, raw, contract("stat evaluation missing")
		}
		if err := ValidateContext(got.Context); err != nil {
			return nil, raw, err
		}
		if !sameIdentity(got.Context.Identity, identity) {
			return nil, raw, contract("stat evaluation world mismatch")
		}
		if got.Stat != stat || math.IsNaN(got.Value) || math.IsInf(got.Value, 0) {
			return nil, raw, contract("invalid stat evaluation")
		}
		return &StatEvaluation{Value: got.Value, ExplanationLines: append([]string(nil), got.ExplanationLines...), Shown: got.Shown}, raw, ctx.Err()
	default:
		return nil, raw, contract("stat evaluation outcome missing")
	}
}

func validStatSubject(subject *o.StatSubject) error {
	switch s := subject.GetSubject().(type) {
	case *o.StatSubject_Definition:
		d := s.Definition
		if d == nil || validID(d.DefName) != nil || d.Stuff != nil && validID(d.GetStuff()) != nil || d.Quality != nil && (d.GetQuality() < 0 || d.GetQuality() > 6) {
			return contract("invalid stat definition subject")
		}
	case *o.StatSubject_ThingId:
		return validID(s.ThingId)
	case *o.StatSubject_PawnId:
		return validID(s.PawnId)
	default:
		return contract("stat subject missing")
	}
	return nil
}
