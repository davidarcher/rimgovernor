package bridge

import (
	"context"

	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

// HusbandryMethod names the direct-write animal management orders:
// recursive training request, the slaughter, tame and release-to-wild
// designations, and the Animals-tab settings (allowed area, master, follow
// flags). The native contract is
// NativeHusbandryOperations.cs (integrations/rimgovernor-native/src/Bridge/Protocol),
// wired onto Operation_SetAnimalTraining/SlaughterAnimal/TameAnimal/
// ReleaseAnimal/SetAnimalArea/SetAnimalMaster/SetAnimalFollowing in
// NativeOperationTools.cs's Execute/Preview dispatch. It ports the legacy
// JSON home/husbandry_config tool's (HusbandryTools.Configure) eligibility
// checks behind the typed boundary: an accepted order is a direct settings
// write (no native job), so acceptance is the effect, not a promise of one.
type HusbandryMethod int32

const (
	HusbandryMethodUnspecified HusbandryMethod = iota
	HusbandryMethodTrain
	HusbandryMethodSlaughter
	HusbandryMethodTame
	HusbandryMethodRelease
	HusbandryMethodAllowedArea
	HusbandryMethodMaster
	HusbandryMethodFollowDrafted
	HusbandryMethodFollowFieldwork
)

var husbandryMethodValid = map[HusbandryMethod]bool{HusbandryMethodTrain: true, HusbandryMethodSlaughter: true, HusbandryMethodTame: true, HusbandryMethodRelease: true,
	HusbandryMethodAllowedArea: true, HusbandryMethodMaster: true, HusbandryMethodFollowDrafted: true, HusbandryMethodFollowFieldwork: true}

// husbandryAssignment is the Assignment an allowed_area/master argument
// encodes: an entity identity, or Clear for the empty argument.
func husbandryAssignment(argument string) *o.Assignment {
	if argument == "" {
		return &o.Assignment{Value: &o.Assignment_Clear{Clear: &o.Clear{}}}
	}
	return &o.Assignment{Value: &o.Assignment_EntityId{EntityId: argument}}
}

type HusbandryAttempt struct {
	Identity            *c.Identity
	Attempt             *c.AttemptKey
	Generation          uint64
	Animal, AnimalToken string
	ExpectedCensusToken string
	Method              HusbandryMethod
	Argument            string
}

func husbandryOperation(animal, animalToken, census, argument string, method HusbandryMethod) *o.Operation {
	entity := gearEntity(animal, animalToken)
	switch method {
	case HusbandryMethodTrain:
		return &o.Operation{Command: &o.Operation_SetAnimalTraining{SetAnimalTraining: &o.SetAnimalTraining{
			Animal: entity, ExpectedCensusToken: proto.String(census), TrainableDef: proto.String(argument),
		}}}
	case HusbandryMethodSlaughter:
		return &o.Operation{Command: &o.Operation_SlaughterAnimal{SlaughterAnimal: &o.SlaughterAnimal{
			Animal: entity, ExpectedCensusToken: proto.String(census),
		}}}
	case HusbandryMethodTame:
		return &o.Operation{Command: &o.Operation_TameAnimal{TameAnimal: &o.TameAnimal{
			Animal: entity, ExpectedCensusToken: proto.String(census),
		}}}
	case HusbandryMethodRelease:
		return &o.Operation{Command: &o.Operation_ReleaseAnimal{ReleaseAnimal: &o.ReleaseAnimal{
			Animal: entity, ExpectedCensusToken: proto.String(census),
		}}}
	case HusbandryMethodAllowedArea:
		return &o.Operation{Command: &o.Operation_SetAnimalArea{SetAnimalArea: &o.SetAnimalArea{
			Animal: entity, ExpectedCensusToken: proto.String(census), Area: husbandryAssignment(argument),
		}}}
	case HusbandryMethodMaster:
		return &o.Operation{Command: &o.Operation_SetAnimalMaster{SetAnimalMaster: &o.SetAnimalMaster{
			Animal: entity, ExpectedCensusToken: proto.String(census), Master: husbandryAssignment(argument),
		}}}
	case HusbandryMethodFollowDrafted:
		return &o.Operation{Command: &o.Operation_SetAnimalFollowing{SetAnimalFollowing: &o.SetAnimalFollowing{
			Animal: entity, ExpectedCensusToken: proto.String(census), FollowDrafted: proto.Bool(argument == "true"),
		}}}
	case HusbandryMethodFollowFieldwork:
		return &o.Operation{Command: &o.Operation_SetAnimalFollowing{SetAnimalFollowing: &o.SetAnimalFollowing{
			Animal: entity, ExpectedCensusToken: proto.String(census), FollowFieldwork: proto.Bool(argument == "true"),
		}}}
	default:
		return &o.Operation{}
	}
}

func husbandryCommand(animal, animalToken, census string, method HusbandryMethod, argument string) error {
	if validID(animal) != nil || validID(animalToken) != nil || validID(census) != nil {
		return contract("invalid husbandry command")
	}
	if !husbandryMethodValid[method] {
		return contract("invalid husbandry method")
	}
	switch method {
	case HusbandryMethodTrain:
		if validID(argument) != nil {
			return contract("invalid husbandry trainable def")
		}
	case HusbandryMethodSlaughter, HusbandryMethodTame, HusbandryMethodRelease:
		if argument != "" {
			return contract("animal designation does not take an argument")
		}
	case HusbandryMethodAllowedArea, HusbandryMethodMaster:
		if argument != "" && validID(argument) != nil {
			return contract("invalid husbandry assignment target")
		}
	case HusbandryMethodFollowDrafted, HusbandryMethodFollowFieldwork:
		if argument != "true" && argument != "false" {
			return contract("invalid husbandry follow flag")
		}
	}
	return nil
}

// PreviewHusbandry checks an exact already-selected training request or
// slaughter/tame/release designation; acceptance is not authority.
func (client *Client) PreviewHusbandry(ctx context.Context, identity *c.Identity, animal, animalToken, census string, method HusbandryMethod, argument string) (*o.PreviewReply, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return nil, Result{}, err
	}
	if err := husbandryCommand(animal, animalToken, census, method, argument); err != nil {
		return nil, Result{}, err
	}
	identity = proto.Clone(identity).(*c.Identity)
	reply := &o.PreviewReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/operations_preview", &o.PreviewRequest{Identity: identity, Operation: husbandryOperation(animal, animalToken, census, argument, method)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.PreviewReply_Failure:
		err = failure(v.Failure, raw)
	case *o.PreviewReply_Evaluated:
		value := v.Evaluated
		if value == nil {
			return reply, raw, contract("husbandry preview missing")
		}
		if err = buildingContext(value.Context, identity, 0, false); err != nil {
			break
		}
		effect := value.Projected.GetAnimal()
		if value.Accepted == nil || !diagnostic(value.Reason) || value.Preparation != nil || effect == nil {
			err = contract("husbandry preview facts missing")
			break
		}
		if effect.Animal.GetEntityId() != animal {
			err = contract("husbandry preview animal mismatch")
		}
	default:
		err = contract("husbandry preview outcome missing")
	}
	return reply, raw, err
}

func husbandryAttempt(v HusbandryAttempt) (HusbandryAttempt, error) {
	if err := ValidateIdentity(v.Identity); err != nil {
		return HusbandryAttempt{}, err
	}
	if err := buildingAttempt(v.Attempt); err != nil {
		return HusbandryAttempt{}, err
	}
	if v.Generation == 0 {
		return HusbandryAttempt{}, contract("husbandry admission owner or generation mismatch")
	}
	if err := husbandryCommand(v.Animal, v.AnimalToken, v.ExpectedCensusToken, v.Method, v.Argument); err != nil {
		return HusbandryAttempt{}, err
	}
	v.Identity = proto.Clone(v.Identity).(*c.Identity)
	v.Attempt = proto.Clone(v.Attempt).(*c.AttemptKey)
	return v, nil
}

func husbandryEvidence(effect *r.AnimalEffect, expected HusbandryAttempt) (*r.AnimalEffect, error) {
	if effect == nil || effect.Animal.GetEntityId() != expected.Animal {
		return nil, contract("husbandry animal mismatch")
	}
	// Each method reports exactly its own effect field; any other field set
	// means the native side answered a different order.
	fields := map[string]bool{
		"trainable": effect.TrainableDef != nil || effect.Wanted != nil,
		"slaughter": effect.SlaughterDesignated != nil, "tame": effect.TameDesignated != nil, "release": effect.ReleaseDesignated != nil,
		"area": effect.AllowedAreaId != nil, "master": effect.MasterId != nil,
		"followDrafted": effect.FollowDrafted != nil, "followFieldwork": effect.FollowFieldwork != nil,
	}
	own := map[HusbandryMethod]string{
		HusbandryMethodTrain: "trainable", HusbandryMethodSlaughter: "slaughter", HusbandryMethodTame: "tame", HusbandryMethodRelease: "release",
		HusbandryMethodAllowedArea: "area", HusbandryMethodMaster: "master", HusbandryMethodFollowDrafted: "followDrafted", HusbandryMethodFollowFieldwork: "followFieldwork",
	}[expected.Method]
	for name, set := range fields {
		if set != (name == own) {
			return nil, contract("husbandry %s effect fields missing or unsupported", own)
		}
	}
	if expected.Method == HusbandryMethodTrain && effect.GetTrainableDef() != expected.Argument {
		return nil, contract("husbandry training effect fields missing or unsupported")
	}
	return effect, nil
}

func husbandryReceipt(v *r.Receipt, expected HusbandryAttempt) error {
	if v == nil || buildingUnknown(v) != nil || !proto.Equal(v.Attempt, expected.Attempt) {
		return contract("husbandry admission mismatch")
	}
	if err := buildingContext(v.AdmittedContext, expected.Identity, expected.Generation, true); err != nil {
		return err
	}
	switch outcome := v.Outcome.(type) {
	case *r.Receipt_Applied:
		if outcome.Applied == nil {
			return contract("husbandry applied missing")
		}
		_, err := husbandryEvidence(outcome.Applied.GetObserved().GetAnimal(), expected)
		return err
	case *r.Receipt_Uncertain:
		if outcome.Uncertain == nil {
			return contract("husbandry uncertainty missing")
		}
		if outcome.Uncertain.LastObserved != nil {
			_, err := husbandryEvidence(outcome.Uncertain.LastObserved.GetAnimal(), expected)
			return err
		}
		return nil
	default:
		return contract("unsupported husbandry receipt")
	}
}

type HusbandryWriter struct{ client *Client }

func NewHusbandryWriter(client *Client) (*HusbandryWriter, error) {
	if client == nil {
		return nil, contract("husbandry client missing")
	}
	return &HusbandryWriter{client}, nil
}

// ApplyHusbandry dispatches one already-admitted training request or
// slaughter/tame/release designation.
func (writer *HusbandryWriter) ApplyHusbandry(ctx context.Context, pre *a.WritePrecondition, animal, animalToken, census string, method HusbandryMethod, argument string) (*o.ExecuteReply, Result, error) {
	if writer == nil || writer.client == nil || pre == nil || buildingUnknown(pre) != nil || ValidateIdentity(pre.Identity) != nil || buildingAttempt(pre.Attempt) != nil || pre.GetExpectedGeneration() == 0 {
		return nil, Result{}, contract("invalid husbandry execution")
	}
	if err := husbandryCommand(animal, animalToken, census, method, argument); err != nil {
		return nil, Result{}, err
	}
	expected, err := husbandryAttempt(HusbandryAttempt{Identity: pre.Identity, Attempt: pre.Attempt, Generation: pre.GetExpectedGeneration(), Animal: animal, AnimalToken: animalToken, ExpectedCensusToken: census, Method: method, Argument: argument})
	if err != nil {
		return nil, Result{}, err
	}
	pre = proto.Clone(pre).(*a.WritePrecondition)
	reply := &o.ExecuteReply{}
	raw, err := writer.client.protoCall(ctx, "rimgovernor/operations_execute", &o.ExecuteRequest{Precondition: pre, Operation: husbandryOperation(animal, animalToken, census, argument, method)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.ExecuteReply_Receipt:
		err = husbandryReceipt(v.Receipt, expected)
	case *o.ExecuteReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("husbandry execute outcome missing")
	}
	return reply, raw, err
}

func (client *Client) LookupHusbandry(ctx context.Context, w HusbandryAttempt) (*r.LookupReply, Result, error) {
	expected, err := husbandryAttempt(w)
	if err != nil {
		return nil, Result{}, err
	}
	reply := &r.LookupReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_lookup", &r.LookupRequest{Identity: expected.Identity, Attempt: expected.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *r.LookupReply_Receipt:
		err = husbandryReceipt(v.Receipt, expected)
	case *r.LookupReply_InFlight:
		if v.InFlight == nil || !proto.Equal(v.InFlight.Attempt, expected.Attempt) {
			err = contract("husbandry in-flight attempt mismatch")
		} else {
			err = buildingContext(v.InFlight.AdmittedContext, expected.Identity, expected.Generation, true)
		}
	case *r.LookupReply_Unknown:
		if v.Unknown == nil {
			err = contract("husbandry unknown context missing")
		} else {
			err = buildingContext(v.Unknown.Context, expected.Identity, 0, false)
		}
	case *r.LookupReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("husbandry lookup outcome missing")
	}
	return reply, raw, err
}

func (client *Client) ObserveHusbandryProgress(ctx context.Context, w HusbandryAttempt, admitted *r.Receipt) (*r.ProgressReply, Result, error) {
	expected, err := husbandryAttempt(w)
	if err != nil {
		return nil, Result{}, err
	}
	if admitted != nil {
		if err = husbandryReceipt(admitted, expected); err != nil {
			return nil, Result{}, err
		}
		admitted = proto.Clone(admitted).(*r.Receipt)
	}
	reply := &r.ProgressReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_observe_progress", &r.ProgressRequest{Identity: expected.Identity, Attempt: expected.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *r.ProgressReply_Progress:
		err = husbandryProgress(v.Progress, expected, admitted)
	case *r.ProgressReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("husbandry progress outcome missing")
	}
	return reply, raw, err
}

func husbandryProgress(v *r.Progress, expected HusbandryAttempt, admitted *r.Receipt) error {
	if v == nil || v.CompleteInspection == nil || !proto.Equal(v.Attempt, expected.Attempt) {
		return contract("husbandry progress attempt mismatch")
	}
	if err := buildingContext(v.Context, expected.Identity, 0, false); err != nil {
		return err
	}
	if admitted != nil && v.Context.GetTick() < admitted.AdmittedContext.GetTick() {
		return contract("husbandry progress predates admission")
	}
	switch outcome := v.Effect.(type) {
	case *r.Progress_Unknown:
		if outcome.Unknown == nil || !diagnostic(outcome.Unknown.Reason) {
			return contract("husbandry unknown progress missing")
		}
		return nil
	case *r.Progress_Completed:
		if outcome.Completed == nil || !v.GetCompleteInspection() {
			return contract("husbandry completed missing")
		}
		_, err := husbandryEvidence(outcome.Completed.Evidence.GetAnimal(), expected)
		return err
	case *r.Progress_Unsuccessful:
		if outcome.Unsuccessful == nil || outcome.Unsuccessful.Reason == nil || outcome.Unsuccessful.GetReason() == 0 || !v.GetCompleteInspection() || !diagnostic(outcome.Unsuccessful.Detail) {
			return contract("husbandry unsuccessful reason missing")
		}
		_, err := husbandryEvidence(outcome.Unsuccessful.Evidence.GetAnimal(), expected)
		return err
	default:
		return contract("husbandry progress state missing")
	}
}
