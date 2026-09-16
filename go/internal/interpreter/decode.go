package interpreter

import (
	"bytes"
	"encoding/json"
	"io"
	"strings"
	"unicode/utf8"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// modelReply is the untrusted decoded shape of one model response: the
// explanation shown to the player and the optional single nudge.
type modelReply struct {
	Explanation string
	Guidance    *modelGuidance
}

// modelGuidance is one decoded nudge. Only the fields matching Kind are
// populated; pointers distinguish required fields from absent or null values.
type modelGuidance struct {
	Kind GuidanceKind
	// Goal holds activate_goal's kind; GoalID holds cancel_goal's exact
	// tracked identity.
	Goal, GoalID *string
	// Maximum/FoodDays hold set_population_policy's cap and food reserve.
	Maximum  *int32
	FoodDays *float64
	// Expedition holds set_expedition_policy's partial patch.
	Expedition *modelExpeditionPolicy
	// Pawn/Decision hold set_population_decision's named individual and
	// direction.
	Pawn, Decision *string
	// Resource and exactly one of Spending/Reserve hold set_resource_policy.
	Resource, Spending *string
	Reserve            *int32
}

// modelExpeditionPolicy is an untrusted partial expedition policy request.
// Pointers distinguish a limit the player asked to change from one that must
// keep whatever value is already in force; at least one must be present.
type modelExpeditionPolicy struct {
	MinimumHomeColonists          *int32   `json:"minimumHomeColonists"`
	MinimumHomeFoodDays           *float64 `json:"minimumHomeFoodDays"`
	TravelFoodMarginDays          *float64 `json:"travelFoodMarginDays"`
	MaximumTravelDays             *float64 `json:"maximumTravelDays"`
	MaximumCaravans               *int32   `json:"maximumCaravans"`
	MinimumGoodwill               *int32   `json:"minimumGoodwill"`
	MinimumDestinationTemperature *float64 `json:"minimumDestinationTemperature"`
	MaximumDestinationTemperature *float64 `json:"maximumDestinationTemperature"`
	KeepHomeDoctor                *bool    `json:"keepHomeDoctor"`
	RequireReturnStorage          *bool    `json:"requireReturnStorage"`
}

const maxExplanationBytes = 16384

func isNull(raw json.RawMessage) bool { return bytes.Equal(bytes.TrimSpace(raw), []byte("null")) }

func decode(text string) (modelReply, error) {
	if len(text) > 65536 || !utf8.ValidString(text) {
		return modelReply{}, fail(InvalidGuidance, "invalid or oversized JSON response")
	}
	decoder := json.NewDecoder(bytes.NewBufferString(text))
	decoder.UseNumber()
	if err := scan(decoder, 0); err != nil {
		return modelReply{}, err
	}
	if _, err := decoder.Token(); err != io.EOF {
		return modelReply{}, fail(InvalidGuidance, "trailing JSON")
	}
	var fields map[string]json.RawMessage
	if err := json.Unmarshal([]byte(text), &fields); err != nil || fields == nil {
		return modelReply{}, fail(InvalidGuidance, "expected reply object")
	}
	if len(fields) != 2 || fields["explanation"] == nil || fields["guidance"] == nil {
		return modelReply{}, fail(InvalidGuidance, "reply requires exactly explanation and guidance")
	}
	var reply modelReply
	if err := json.Unmarshal(fields["explanation"], &reply.Explanation); err != nil || strings.TrimSpace(reply.Explanation) == "" || len(reply.Explanation) > maxExplanationBytes {
		return modelReply{}, fail(InvalidGuidance, "invalid explanation")
	}
	if isNull(fields["guidance"]) {
		return reply, nil
	}
	var guidanceFields map[string]json.RawMessage
	if err := json.Unmarshal(fields["guidance"], &guidanceFields); err != nil || guidanceFields == nil {
		return modelReply{}, fail(InvalidGuidance, "expected guidance object or null")
	}
	var kind *string
	if err := json.Unmarshal(guidanceFields["kind"], &kind); err != nil || kind == nil {
		return modelReply{}, fail(InvalidGuidance, "missing guidance kind")
	}
	var guidance modelGuidance
	var err error
	switch GuidanceKind(*kind) {
	case ActivateGoal:
		guidance, err = decodeOneString(guidanceFields, ActivateGoal, "goal")
	case CancelGoal:
		guidance, err = decodeOneString(guidanceFields, CancelGoal, "goalId")
	case SetPopulationPolicy:
		guidance, err = decodeSetPopulationPolicy(guidanceFields)
	case SetExpeditionPolicy:
		guidance, err = decodeSetExpeditionPolicy(guidanceFields)
	case SetPopulationDecision:
		guidance, err = decodeSetPopulationDecision(guidanceFields)
	case SetResourcePolicy:
		guidance, err = decodeSetResourcePolicy(guidanceFields)
	default:
		return modelReply{}, fail(InvalidGuidance, "unsupported guidance kind")
	}
	if err != nil {
		return modelReply{}, err
	}
	reply.Guidance = &guidance
	return reply, nil
}

// decodeOneString reads the single string field activate_goal and cancel_goal
// each carry. Membership (whitelisted kind, tracked identity) belongs to the
// interpreter's bounding; this only rejects the wrong shape.
func decodeOneString(fields map[string]json.RawMessage, kind GuidanceKind, key string) (modelGuidance, error) {
	if len(fields) != 2 || fields[key] == nil {
		return modelGuidance{}, fail(InvalidGuidance, "unexpected guidance fields")
	}
	var value string
	if err := json.Unmarshal(fields[key], &value); err != nil || value == "" {
		return modelGuidance{}, fail(InvalidGuidance, "invalid "+key+" field")
	}
	g := modelGuidance{Kind: kind}
	if kind == ActivateGoal {
		g.Goal = &value
	} else {
		g.GoalID = &value
	}
	return g, nil
}

// decodeSetPopulationPolicy reads the two bounded numbers; the range check
// belongs to domain.NewPopulationPolicy.
func decodeSetPopulationPolicy(fields map[string]json.RawMessage) (modelGuidance, error) {
	if len(fields) != 3 || fields["maximum"] == nil || fields["foodDays"] == nil || isNull(fields["maximum"]) || isNull(fields["foodDays"]) {
		return modelGuidance{}, fail(InvalidGuidance, "unexpected guidance fields")
	}
	var maximum int32
	if err := json.Unmarshal(fields["maximum"], &maximum); err != nil {
		return modelGuidance{}, fail(InvalidGuidance, "invalid maximum field")
	}
	var foodDays float64
	if err := json.Unmarshal(fields["foodDays"], &foodDays); err != nil {
		return modelGuidance{}, fail(InvalidGuidance, "invalid foodDays field")
	}
	return modelGuidance{Kind: SetPopulationPolicy, Maximum: &maximum, FoodDays: &foodDays}, nil
}

// decodeSetPopulationDecision reads the pawn and direction; the vocabulary
// belongs to domain.NewPopulationDirective and the pawn is bounded against
// facts by the interpreter.
func decodeSetPopulationDecision(fields map[string]json.RawMessage) (modelGuidance, error) {
	if len(fields) != 3 || fields["pawn"] == nil || fields["decision"] == nil {
		return modelGuidance{}, fail(InvalidGuidance, "unexpected guidance fields")
	}
	var pawn string
	if err := json.Unmarshal(fields["pawn"], &pawn); err != nil || pawn == "" {
		return modelGuidance{}, fail(InvalidGuidance, "invalid pawn field")
	}
	var decision string
	if err := json.Unmarshal(fields["decision"], &decision); err != nil || decision == "" {
		return modelGuidance{}, fail(InvalidGuidance, "invalid decision field")
	}
	return modelGuidance{Kind: SetPopulationDecision, Pawn: &pawn, Decision: &decision}, nil
}

// decodeSetResourcePolicy reads the resource and exactly one of spending or
// reserve; the vocabulary and range belong to domain.ResourcePolicyPatch and
// the resource is bounded against facts by the interpreter.
func decodeSetResourcePolicy(fields map[string]json.RawMessage) (modelGuidance, error) {
	if len(fields) != 3 || fields["resource"] == nil || (fields["spending"] == nil) == (fields["reserve"] == nil) {
		return modelGuidance{}, fail(InvalidGuidance, "unexpected guidance fields")
	}
	var resource string
	if err := json.Unmarshal(fields["resource"], &resource); err != nil || resource == "" {
		return modelGuidance{}, fail(InvalidGuidance, "invalid resource field")
	}
	g := modelGuidance{Kind: SetResourcePolicy, Resource: &resource}
	if fields["spending"] != nil {
		var spending string
		if err := json.Unmarshal(fields["spending"], &spending); err != nil || spending == "" {
			return modelGuidance{}, fail(InvalidGuidance, "invalid spending field")
		}
		g.Spending = &spending
		return g, nil
	}
	if isNull(fields["reserve"]) {
		return modelGuidance{}, fail(InvalidGuidance, "invalid reserve field")
	}
	var reserve int32
	if err := json.Unmarshal(fields["reserve"], &reserve); err != nil {
		return modelGuidance{}, fail(InvalidGuidance, "invalid reserve field")
	}
	g.Reserve = &reserve
	return g, nil
}

// optionalField turns a decoded partial-request pointer into the comparable
// domain.Optional the domain and store layers carry.
func optionalField[T comparable](value *T) domain.Optional[T] {
	if value == nil {
		return domain.Optional[T]{}
	}
	return domain.Some(*value)
}

// expeditionPolicyFieldNames is the closed set of limits a
// set_expedition_policy request may name.
var expeditionPolicyFieldNames = map[string]bool{
	"minimumHomeColonists": true, "minimumHomeFoodDays": true, "travelFoodMarginDays": true,
	"maximumTravelDays": true, "maximumCaravans": true, "minimumGoodwill": true,
	"minimumDestinationTemperature": true, "maximumDestinationTemperature": true,
	"keepHomeDoctor": true, "requireReturnStorage": true,
}

// decodeSetExpeditionPolicy reads the subset of expedition limits the request
// names. The field set is variable because the request is a partial patch:
// an absent limit keeps whatever value is already in force, so only unknown
// names, nulls and an empty request are refused on shape. Ranges and the
// destination-temperature cross-check belong to domain.ExpeditionPolicyPatch.
func decodeSetExpeditionPolicy(fields map[string]json.RawMessage) (modelGuidance, error) {
	if len(fields) < 2 {
		return modelGuidance{}, fail(InvalidGuidance, "unexpected guidance fields")
	}
	supplied := make(map[string]json.RawMessage, len(fields)-1)
	for key, value := range fields {
		if key == "kind" {
			continue
		}
		if !expeditionPolicyFieldNames[key] || isNull(value) {
			return modelGuidance{}, fail(InvalidGuidance, "unexpected guidance fields")
		}
		supplied[key] = value
	}
	payload, err := json.Marshal(supplied)
	if err != nil {
		return modelGuidance{}, fail(InvalidGuidance, "unexpected guidance fields")
	}
	var policy modelExpeditionPolicy
	if err = json.Unmarshal(payload, &policy); err != nil {
		return modelGuidance{}, fail(InvalidGuidance, "invalid expedition policy field")
	}
	return modelGuidance{Kind: SetExpeditionPolicy, Expedition: &policy}, nil
}

// Generic JSON token inspection is confined to this external text boundary.
func scan(decoder *json.Decoder, depth int) error {
	if depth > 16 {
		return fail(InvalidGuidance, "JSON nesting exceeds limit")
	}
	token, err := decoder.Token()
	if err != nil {
		return fail(InvalidGuidance, "malformed JSON")
	}
	delimiter, container := token.(json.Delim)
	if !container {
		return nil
	}
	seen := map[string]bool{}
	for decoder.More() {
		if delimiter == '{' {
			key, err := decoder.Token()
			if err != nil {
				return fail(InvalidGuidance, "malformed JSON key")
			}
			name, ok := key.(string)
			if !ok || seen[name] {
				return fail(InvalidGuidance, "duplicate JSON key")
			}
			seen[name] = true
		}
		if err := scan(decoder, depth+1); err != nil {
			return err
		}
	}
	if _, err := decoder.Token(); err != nil {
		return fail(InvalidGuidance, "malformed JSON container")
	}
	return nil
}
