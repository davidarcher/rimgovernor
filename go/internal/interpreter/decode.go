package interpreter

import (
	"bytes"
	"encoding/json"
	"io"
	"unicode/utf8"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// modelBuilding is an untrusted proposal, separate from native transport types.
// Pointers distinguish required fields from absent or null values.
type modelBuilding struct {
	DefName  *string `json:"defName"`
	X        *int32  `json:"x"`
	Z        *int32  `json:"z"`
	Rotation *string `json:"rotation"`
	Stuff    *string `json:"stuff"`
}

// modelCommand is the untrusted decoded shape of exactly one supported
// command. Only the fields matching Command are populated.
type modelCommand struct {
	Command   string
	Buildings []modelBuilding
	Project   *string
	// Pawn holds set_population_decision's named individual, which must be
	// an observed pawn, so unlike the two policy commands it is bounded
	// against supplied facts.
	Pawn *string
	// Maximum/FoodDays hold set_population_policy's requested colonist cap
	// and minimum stored-food reserve in days. Both are plain bounded
	// numbers: unlike every other command here this one names no observed
	// entity, so there is nothing to bound against facts.
	Maximum  *int32
	FoodDays *float64
	// ExpeditionPolicy holds set_expedition_policy's requested subset of the
	// expedition risk limits. Unlike every other command here the request is
	// a partial patch, so absence is meaningful and the field stays nil when
	// the command is something else.
	ExpeditionPolicy *modelExpeditionPolicy
	// Resource holds modify_resource_policy's and set_resource_reserve's named
	// resource definition, which must be an observed one; Spending holds the
	// former's requested restriction ("normal", "defense_only" or "stop") and
	// Reserve the latter's requested protected quantity. The two commands are
	// separate contracts that each set exactly one half, so exactly one of
	// Spending and Reserve is ever populated.
	Resource, Spending *string
	Reserve            *int32
	// Decision holds set_population_decision's requested per-pawn direction
	// ("rescue", "capture", "recruit" or "ignore").
	Decision *string
	// Goal holds create_goal's requested maintained goal kind (from a fixed
	// whitelist, so nothing to bound against facts) or cancel_goal's target
	// goal identity (which must be an observed one). The two commands are
	// separate contracts that never appear together, so they share the field.
	Goal *string
}

// modelExpeditionPolicy is an untrusted partial expedition policy request.
// Pointers distinguish a limit the player asked to change from one that must
// keep whatever value is already in force; at least one must be present.
// Like set_population_policy these are plain bounded numbers and flags that
// name no observed entity, so there is nothing to bound against facts.
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

func decode(text string, limit int) (modelCommand, error) {
	if len(text) > 65536 || !utf8.ValidString(text) {
		return modelCommand{}, fail(InvalidCommand, "invalid or oversized JSON response")
	}
	decoder := json.NewDecoder(bytes.NewBufferString(text))
	decoder.UseNumber()
	if err := scan(decoder, 0); err != nil {
		return modelCommand{}, err
	}
	if _, err := decoder.Token(); err != io.EOF {
		return modelCommand{}, fail(InvalidCommand, "trailing JSON")
	}
	var fields map[string]json.RawMessage
	if err := json.Unmarshal([]byte(text), &fields); err != nil || fields == nil {
		return modelCommand{}, fail(InvalidCommand, "expected command object")
	}
	var command *string
	if err := json.Unmarshal(fields["command"], &command); err != nil || command == nil {
		return modelCommand{}, fail(InvalidCommand, "missing command")
	}
	switch *command {
	case "build":
		return decodeBuild(fields, limit)
	case "research":
		return decodeResearch(fields)
	case "set_population_policy":
		return decodeSetPopulationPolicy(fields)
	case "set_expedition_policy":
		return decodeSetExpeditionPolicy(fields)
	case "set_population_decision":
		return decodeSetPopulationDecision(fields)
	case "modify_resource_policy":
		return decodeModifyResourcePolicy(fields)
	case "set_resource_reserve":
		return decodeSetResourceReserve(fields)
	case "create_goal":
		return decodeGoalCommand(fields, "create_goal")
	case "cancel_goal":
		return decodeGoalCommand(fields, "cancel_goal")
	case "evaluate_world":
		return decodeEvaluateWorld(fields)
	default:
		return modelCommand{}, fail(UnsupportedCommand, "only build, research, set_population_policy, set_expedition_policy, set_population_decision, modify_resource_policy, set_resource_reserve, create_goal, cancel_goal and evaluate_world proposals are supported")
	}
}

func decodeBuild(fields map[string]json.RawMessage, limit int) (modelCommand, error) {
	if len(fields) != 2 || fields["buildings"] == nil {
		return modelCommand{}, fail(InvalidCommand, "unexpected command fields")
	}
	var buildings []json.RawMessage
	if err := json.Unmarshal(fields["buildings"], &buildings); err != nil || len(buildings) == 0 || len(buildings) > limit {
		return modelCommand{}, fail(InvalidCommand, "expected bounded nonempty building list")
	}
	result := make([]modelBuilding, 0, len(buildings))
	for _, raw := range buildings {
		var fields map[string]json.RawMessage
		if err := json.Unmarshal(raw, &fields); err != nil {
			return modelCommand{}, fail(InvalidCommand, "expected building object")
		}
		if len(fields) != 5 {
			return modelCommand{}, fail(InvalidCommand, "building requires five exact fields")
		}
		for _, key := range []string{"defName", "x", "z", "rotation", "stuff"} {
			if fields[key] == nil || bytes.Equal(bytes.TrimSpace(fields[key]), []byte("null")) {
				return modelCommand{}, fail(InvalidCommand, "missing or null building field")
			}
		}
		var b modelBuilding
		if err := json.Unmarshal(raw, &b); err != nil {
			return modelCommand{}, fail(InvalidCommand, "invalid building field type")
		}
		result = append(result, b)
	}
	return modelCommand{Command: "build", Buildings: result}, nil
}

func decodeResearch(fields map[string]json.RawMessage) (modelCommand, error) {
	if len(fields) != 2 || fields["project"] == nil || bytes.Equal(bytes.TrimSpace(fields["project"]), []byte("null")) {
		return modelCommand{}, fail(InvalidCommand, "unexpected command fields")
	}
	var project string
	if err := json.Unmarshal(fields["project"], &project); err != nil || project == "" {
		return modelCommand{}, fail(InvalidCommand, "invalid project field")
	}
	return modelCommand{Command: "research", Project: &project}, nil
}

// decodeSetPopulationPolicy reads the two bounded numbers the population
// capacity policy carries. The range check itself belongs to
// domain.NewPopulationPolicy; this only rejects the wrong shape.
func decodeSetPopulationPolicy(fields map[string]json.RawMessage) (modelCommand, error) {
	if len(fields) != 3 || fields["maximum"] == nil || fields["foodDays"] == nil || bytes.Equal(bytes.TrimSpace(fields["maximum"]), []byte("null")) || bytes.Equal(bytes.TrimSpace(fields["foodDays"]), []byte("null")) {
		return modelCommand{}, fail(InvalidCommand, "unexpected command fields")
	}
	var maximum int32
	if err := json.Unmarshal(fields["maximum"], &maximum); err != nil {
		return modelCommand{}, fail(InvalidCommand, "invalid maximum field")
	}
	var foodDays float64
	if err := json.Unmarshal(fields["foodDays"], &foodDays); err != nil {
		return modelCommand{}, fail(InvalidCommand, "invalid foodDays field")
	}
	return modelCommand{Command: "set_population_policy", Maximum: &maximum, FoodDays: &foodDays}, nil
}

// decodeSetPopulationDecision reads the pawn and direction a per-pawn
// population decision names. The decision vocabulary itself belongs to
// domain.NewPopulationDirective and the pawn is bounded against supplied
// facts by the interpreter; this only rejects the wrong shape.
func decodeSetPopulationDecision(fields map[string]json.RawMessage) (modelCommand, error) {
	if len(fields) != 3 || fields["pawn"] == nil || fields["decision"] == nil {
		return modelCommand{}, fail(InvalidCommand, "unexpected command fields")
	}
	var pawn string
	if err := json.Unmarshal(fields["pawn"], &pawn); err != nil || pawn == "" {
		return modelCommand{}, fail(InvalidCommand, "invalid pawn field")
	}
	var decision string
	if err := json.Unmarshal(fields["decision"], &decision); err != nil || decision == "" {
		return modelCommand{}, fail(InvalidCommand, "invalid decision field")
	}
	return modelCommand{Command: "set_population_decision", Pawn: &pawn, Decision: &decision}, nil
}

// decodeModifyResourcePolicy reads the resource and spending restriction
// ModifyResourcePolicy names. The restriction vocabulary belongs to
// domain.ResourcePolicyPatch and the resource is bounded against supplied facts
// by the interpreter; this only rejects the wrong shape. Deliberately no
// reserve field: the Python contract sets spending alone and preserves the
// existing reserve.
func decodeModifyResourcePolicy(fields map[string]json.RawMessage) (modelCommand, error) {
	if len(fields) != 3 || fields["resource"] == nil || fields["spending"] == nil {
		return modelCommand{}, fail(InvalidCommand, "unexpected command fields")
	}
	var resource string
	if err := json.Unmarshal(fields["resource"], &resource); err != nil || resource == "" {
		return modelCommand{}, fail(InvalidCommand, "invalid resource field")
	}
	var spending string
	if err := json.Unmarshal(fields["spending"], &spending); err != nil || spending == "" {
		return modelCommand{}, fail(InvalidCommand, "invalid spending field")
	}
	return modelCommand{Command: "modify_resource_policy", Resource: &resource, Spending: &spending}, nil
}

// decodeSetResourceReserve reads the resource and protected quantity
// SetResourceReserve names. The range belongs to domain.ResourcePolicyPatch;
// this only rejects the wrong shape. Deliberately no spending field: the Python
// contract sets the reserve alone and preserves the existing restriction.
func decodeSetResourceReserve(fields map[string]json.RawMessage) (modelCommand, error) {
	if len(fields) != 3 || fields["resource"] == nil || fields["reserve"] == nil || bytes.Equal(bytes.TrimSpace(fields["reserve"]), []byte("null")) {
		return modelCommand{}, fail(InvalidCommand, "unexpected command fields")
	}
	var resource string
	if err := json.Unmarshal(fields["resource"], &resource); err != nil || resource == "" {
		return modelCommand{}, fail(InvalidCommand, "invalid resource field")
	}
	var reserve int32
	if err := json.Unmarshal(fields["reserve"], &reserve); err != nil {
		return modelCommand{}, fail(InvalidCommand, "invalid reserve field")
	}
	return modelCommand{Command: "set_resource_reserve", Resource: &resource, Reserve: &reserve}, nil
}

// decodeGoalCommand reads the single goal field create_goal and cancel_goal
// each carry. The two contracts have the same one-string shape and differ only
// in what that string must be -- a whitelisted kind for create_goal, an
// observed identity for cancel_goal -- so the membership check belongs to the
// interpreter's own bounding, and this only rejects the wrong shape.
// Deliberately no target fields: unlike Python's CreateGoal there is no
// food_days, resource, quantity, deep_extraction, unwanted or bury here, and
// any of them present makes the field count wrong and the command invalid.
func decodeGoalCommand(fields map[string]json.RawMessage, command string) (modelCommand, error) {
	if len(fields) != 2 || fields["goal"] == nil {
		return modelCommand{}, fail(InvalidCommand, "unexpected command fields")
	}
	var goal string
	if err := json.Unmarshal(fields["goal"], &goal); err != nil || goal == "" {
		return modelCommand{}, fail(InvalidCommand, "invalid goal field")
	}
	return modelCommand{Command: command, Goal: &goal}, nil
}

// decodeEvaluateWorld reads the only command in this family that carries no
// argument at all. Python's EvaluateWorld contract declares nothing but its
// kind discriminator, because the advisory reports on whatever caravans and
// quests native currently shows: there is no target to name and therefore
// nothing to bound against facts, which makes the exact field count -- the
// "command" key and nothing else -- the whole of its shape rule.
//
// Anything extra is refused rather than ignored, exactly as decodeGoalCommand
// refuses Python's per-goal target fields: a model that supplied a caravan, a
// quest or a scope here would be asking for a filtered evaluation this command
// does not offer, and silently dropping the filter would answer a different
// question than the one it asked.
func decodeEvaluateWorld(fields map[string]json.RawMessage) (modelCommand, error) {
	if len(fields) != 1 {
		return modelCommand{}, fail(InvalidCommand, "unexpected command fields")
	}
	return modelCommand{Command: "evaluate_world"}, nil
}

// optionalCommandField turns a decoded partial-request pointer into the
// comparable domain.Optional the domain and store layers carry.
func optionalCommandField[T comparable](value *T) domain.Optional[T] {
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
// names. Unlike every other command here the field set is variable, because
// the request is a partial patch: an absent limit keeps whatever value is
// already in force, so only unknown names, nulls and an empty request are
// refused on shape. The numeric ranges and the destination-temperature
// cross-check belong to domain.ExpeditionPolicyPatch.
func decodeSetExpeditionPolicy(fields map[string]json.RawMessage) (modelCommand, error) {
	if len(fields) < 2 {
		return modelCommand{}, fail(InvalidCommand, "unexpected command fields")
	}
	supplied := make(map[string]json.RawMessage, len(fields)-1)
	for key, value := range fields {
		if key == "command" {
			continue
		}
		if !expeditionPolicyFieldNames[key] || bytes.Equal(bytes.TrimSpace(value), []byte("null")) {
			return modelCommand{}, fail(InvalidCommand, "unexpected command fields")
		}
		supplied[key] = value
	}
	payload, err := json.Marshal(supplied)
	if err != nil {
		return modelCommand{}, fail(InvalidCommand, "unexpected command fields")
	}
	var policy modelExpeditionPolicy
	if err = json.Unmarshal(payload, &policy); err != nil {
		return modelCommand{}, fail(InvalidCommand, "invalid expedition policy field")
	}
	return modelCommand{Command: "set_expedition_policy", ExpeditionPolicy: &policy}, nil
}

// Generic JSON token inspection is confined to this external text boundary.
func scan(decoder *json.Decoder, depth int) error {
	if depth > 16 {
		return fail(InvalidCommand, "JSON nesting exceeds limit")
	}
	token, err := decoder.Token()
	if err != nil {
		return fail(InvalidCommand, "malformed JSON")
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
				return fail(InvalidCommand, "malformed JSON key")
			}
			name, ok := key.(string)
			if !ok || seen[name] {
				return fail(InvalidCommand, "duplicate JSON key")
			}
			seen[name] = true
		}
		if err := scan(decoder, depth+1); err != nil {
			return err
		}
	}
	if _, err := decoder.Token(); err != nil {
		return fail(InvalidCommand, "malformed JSON container")
	}
	return nil
}
