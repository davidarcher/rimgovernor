package bridge

import (
	"context"
	"errors"
	"math"
	"regexp"

	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	"google.golang.org/protobuf/proto"
)

// Bounds a NewColonyRequest must respect; the HTTP route and native share them.
const (
	NewColonyMaxColonists   = 10
	NewColonyMinMapSize     = 100
	NewColonyMaxMapSize     = 400
	NewColonyMinCoverage    = 0.05
	NewColonyMaxCoverage    = 1.0
	NewColonyMinTimeoutMs   = 1000
	NewColonyMaxTimeoutMs   = 1_800_000
	newColonyMaxBiomes      = 32
	NewColonyMaxTemperature = 200
)

var newColonySaveName = regexp.MustCompile(`^[A-Za-z0-9_-]{1,64}$`)

// ErrNewColonySuperseded means a NewColony/ReadNewColony request no longer
// names the generation native is tracking. Never a license to retry blindly.
var ErrNewColonySuperseded = errors.New("native new colony outcome superseded")

type NewColonySuperseded struct {
	Value   *l.NewColonySuperseded
	Receipt Result
}

func (e *NewColonySuperseded) Error() string {
	return "native new colony outcome superseded: " + e.Value.GetDetail()
}
func (e *NewColonySuperseded) Unwrap() error { return ErrNewColonySuperseded }

// ErrNewColonyPending means the colony is still generating; the caller polls
// ReadNewColony again. It is returned as an error so a nil reply is never
// mistaken for success.
var ErrNewColonyPending = errors.New("native new colony still pending")

type NewColonyPending struct {
	Value   *l.NewColonyPending
	Receipt Result
}

func (e *NewColonyPending) Error() string {
	return "native new colony still pending: " + e.Value.GetPhase().String()
}
func (e *NewColonyPending) Unwrap() error { return ErrNewColonyPending }

// LifecycleNewColony is a separately held mutation capability, like
// LifecycleLoad: possession of a Client alone grants no authority to start a colony.
type LifecycleNewColony struct{ client *Client }

func NewLifecycleNewColony(client *Client) (*LifecycleNewColony, error) {
	if client == nil {
		return nil, contract("lifecycle new colony client required")
	}
	return &LifecycleNewColony{client}, nil
}

// NewColony starts generating a colony from the request's spec and returns
// immediately: a Pending refusal (the ordinary case) the caller polls with
// ReadNewColony, or a Failure for a pre-flight rejection.
func (n *LifecycleNewColony) NewColony(ctx context.Context, request *l.NewColonyRequest) (*l.NewColonyReply, Result, error) {
	if n == nil || n.client == nil {
		return nil, Result{}, contract("lifecycle new colony capability required")
	}
	if err := ValidateNewColonyRequest(request); err != nil {
		return nil, Result{}, err
	}
	request = proto.Clone(request).(*l.NewColonyRequest)
	if request.Spec.GetGovernorIdeoligion() && request.Spec.Ideoligion == nil {
		catalog, raw, err := n.client.CreationCatalog(ctx)
		if err != nil {
			return nil, raw, err
		}
		choice, err := catalog.StartingIdeoligion(request.Spec.GetScenario())
		if err != nil {
			return nil, raw, err
		}
		request.Spec.Ideoligion = WireIdeoligionDesign(choice.Design)
	}
	request.Spec.GovernorIdeoligion = nil
	reply := &l.NewColonyReply{}
	raw, err := n.client.protoCall(ctx, "rimgovernor/lifecycle_new_colony", request, reply)
	if err != nil {
		return nil, raw, err
	}
	return interpretNewColonyReply(reply, request.RequestId, request.Spec.SaveName, raw)
}

// ReadNewColony polls the outcome of a request_id a prior NewColony call started.
func (n *LifecycleNewColony) ReadNewColony(ctx context.Context, requestID string) (*l.NewColonyReply, Result, error) {
	if n == nil || n.client == nil {
		return nil, Result{}, contract("lifecycle new colony capability required")
	}
	if validID(requestID) != nil {
		return nil, Result{}, contract("read new colony requires a request id")
	}
	request := &l.RequestStatus{RequestId: proto.String(requestID)}
	reply := &l.NewColonyReply{}
	raw, err := n.client.protoCall(ctx, "rimgovernor/lifecycle_read_new_colony", request, reply)
	if err != nil {
		return nil, raw, err
	}
	return interpretNewColonyReply(reply, request.RequestId, nil, raw)
}

func interpretNewColonyReply(reply *l.NewColonyReply, requestID, saveName *string, raw Result) (*l.NewColonyReply, Result, error) {
	switch value := reply.Outcome.(type) {
	case *l.NewColonyReply_Completed:
		if err := validateNewColonyCompleted(value.Completed, requestID, saveName); err != nil {
			return nil, raw, err
		}
	case *l.NewColonyReply_Pending:
		p := value.Pending
		if p == nil || p.RequestId == nil || requestID != nil && p.GetRequestId() != *requestID || !diagnostic(p.Detail) || !validNewColonyPhase(p.Phase) {
			return nil, raw, contract("invalid new colony pending")
		}
		return nil, raw, &NewColonyPending{p, raw}
	case *l.NewColonyReply_Superseded:
		s := value.Superseded
		if s == nil || s.RequestId == nil || requestID != nil && s.GetRequestId() != *requestID || !diagnostic(s.Detail) {
			return nil, raw, contract("invalid new colony superseded")
		}
		return nil, raw, &NewColonySuperseded{s, raw}
	case *l.NewColonyReply_Failure:
		return nil, raw, failure(value.Failure, raw)
	default:
		return nil, raw, contract("new colony outcome missing")
	}
	return reply, raw, nil
}

func validNewColonyPhase(phase *l.NewColonyPhase) bool {
	if phase == nil || *phase == l.NewColonyPhase_NEW_COLONY_PHASE_UNSPECIFIED {
		return false
	}
	_, ok := l.NewColonyPhase_name[int32(*phase)]
	return ok
}

// ValidateNewColonyRequest checks a request against every bound the wire
// contract names, before it reaches native.
func ValidateNewColonyRequest(request *l.NewColonyRequest) error {
	if request == nil {
		return contract("new colony request required")
	}
	if request.RequestId == nil || validID(request.GetRequestId()) != nil {
		return contract("new colony request requires a request id")
	}
	if request.TimeoutMs == nil || request.GetTimeoutMs() < NewColonyMinTimeoutMs || request.GetTimeoutMs() > NewColonyMaxTimeoutMs {
		return contract("new colony timeout out of range")
	}
	return ValidateNewColonySpec(request.Spec)
}

// ValidateNewColonySpec checks the spec's presence and range rules. Whether a
// defName exists is native's call; it refuses unknown names and lists valid ones.
func ValidateNewColonySpec(spec *l.NewColonySpec) error {
	if spec == nil {
		return contract("new colony spec required")
	}
	for name, value := range map[string]*string{
		"scenario": spec.Scenario, "seed": spec.Seed, "difficulty": spec.Difficulty, "storyteller": spec.Storyteller,
	} {
		if value == nil || validID(*value) != nil {
			return contract("new colony spec requires %s", name)
		}
	}
	if spec.WorldTemperature != nil && validID(*spec.WorldTemperature) != nil {
		return contract("new colony world temperature invalid")
	}
	if spec.ColonistCount == nil || spec.GetColonistCount() < 1 || spec.GetColonistCount() > NewColonyMaxColonists {
		return contract("new colony colonist count out of range")
	}
	if len(spec.Biomes) > newColonyMaxBiomes || !validIDs(spec.Biomes, true) {
		return contract("new colony biomes invalid")
	}
	if spec.MapSize == nil || spec.GetMapSize() < NewColonyMinMapSize || spec.GetMapSize() > NewColonyMaxMapSize {
		return contract("new colony map size out of range")
	}
	coverage := float64(spec.GetPlanetCoverage())
	if spec.PlanetCoverage == nil || math.IsNaN(coverage) || coverage < NewColonyMinCoverage || coverage > NewColonyMaxCoverage {
		return contract("new colony planet coverage out of range")
	}
	for name, value := range map[string]*float32{"min": spec.MinTemperature, "max": spec.MaxTemperature} {
		if value != nil && (math.IsNaN(float64(*value)) || math.Abs(float64(*value)) > NewColonyMaxTemperature) {
			return contract("new colony %s temperature out of range", name)
		}
	}
	if spec.MinTemperature != nil && spec.MaxTemperature != nil && spec.GetMinTemperature() > spec.GetMaxTemperature() {
		return contract("new colony min temperature exceeds max")
	}
	if spec.SaveName == nil || !newColonySaveName.MatchString(spec.GetSaveName()) {
		return contract("new colony save name invalid")
	}
	if spec.Ideoligion != nil {
		return ValidateIdeoligionDesign(spec.Ideoligion)
	}
	return nil
}

func validateNewColonyCompleted(completed *l.NewColonyCompleted, requestID, saveName *string) error {
	if completed == nil {
		return contract("missing completed new colony")
	}
	if err := ValidateContext(completed.Context); err != nil {
		return err
	}
	if completed.RequestId == nil || requestID != nil && completed.GetRequestId() != *requestID {
		return contract("completed new colony request id mismatch")
	}
	if completed.SaveName == nil || saveName != nil && completed.GetSaveName() != *saveName {
		return contract("completed new colony save name mismatch")
	}
	if completed.Paused == nil || completed.ByteLength == nil || completed.Seed == nil || validID(completed.GetSeed()) != nil {
		return contract("completed new colony missing paused, byte length or seed")
	}
	return nil
}
