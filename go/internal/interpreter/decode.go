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

// modelCargo is one untrusted requested caravan cargo line.
type modelCargo struct {
	Definition *string `json:"defName"`
	Count      *uint64 `json:"count"`
}

// modelCell is one untrusted requested cell: a zone footprint cell, or one cell
// of an adopted room's exact interior.
type modelCell struct {
	X *int32 `json:"x"`
	Z *int32 `json:"z"`
}

// modelCommand is the untrusted decoded shape of exactly one supported
// command. Only the fields matching Command are populated.
type modelCommand struct {
	Command   string
	Buildings []modelBuilding
	Project   *string
	// First/Second hold tend's doctor/patient, rescue's rescuer/patient, or
	// draft's sole pawn, in that role order; the field names are shared
	// since these are the same one/two-distinct-pawn shapes.
	First, Second   *string
	Crew            []string
	Cargo           []modelCargo
	DestinationTile *int32
	// Animal/Method/TrainableDef hold husbandry's target, train-or-slaughter
	// choice, and (train only) trainable definition.
	Animal, Method, TrainableDef *string
	// Pawn/Thing/Service hold recover's pawn, service target thing, and
	// repair/breakdown/refuel method (a distinct field from husbandry's Method).
	Pawn, Thing, Service *string
	// Bed holds bed_assign's target bed thing ID (Pawn is shared with recover).
	Bed *string
	// X/Z hold move_pawn's destination cell (Pawn is shared with recover/bed_assign).
	X, Z *int32
	// Celsius holds set_building_temperature's requested target temperature
	// (Thing is shared with recover's service target field).
	Celsius *float64
	// Recipe/Part hold request_surgery's recipe def name and body part index
	// (-1 for whole-body); the patient reuses Pawn.
	Recipe *string
	Part   *int32
	// Caravan holds hold_caravan/route_caravan's target already-formed player
	// caravan ID. ReturnHome/VisitSettlement select route_caravan's
	// disposition; DestinationTile (shared with caravan departure) is
	// required for a route and absent for return_home.
	Caravan                     *string
	ReturnHome, VisitSettlement *bool
	// Quest holds accept_quest/fulfill_quest's target quest ID.
	Quest *string
	// AccepterPawn/RewardChoice hold accept_quest's accepter pawn (empty
	// string when none is required) and reward option index (-1 when the
	// quest carries none).
	AccepterPawn *string
	RewardChoice *int32
	// Settlement/Faction hold gift_settlement's target settlement and
	// faction (Caravan and Crew, shared with caravan departure/fulfillment,
	// hold its caravan and crew). Silver holds the requested gift amount.
	Settlement, Faction *string
	Silver              *int32
	// ZoneKind/Preset/Priority hold create_zone's zone kind ("growing" or
	// "stockpile"), stockpile filter preset ("food" or "nothing") and
	// priority ("important"); Crop holds the growing kind's sown crop def.
	// ZoneCells holds the requested footprint and Allow the nothing
	// preset's allow-list definitions.
	ZoneKind, Preset, Priority, Crop *string
	ZoneCells                        []modelCell
	Allow                            []string
	// ZoneID/ZoneOp hold edit_zone's target zone ID and operation ("add",
	// "remove" or "delete"); ZoneCells (shared with create_zone) holds the
	// requested delta cells for add/remove and is unused for delete.
	ZoneID, ZoneOp *string
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
	// ("rescue", "capture", "recruit" or "ignore"); the named individual
	// reuses Pawn and must be an observed pawn, so unlike the two policy
	// commands this one is bounded against supplied facts.
	Decision *string
	// IntentID/Room hold build_room's player-chosen construction intent
	// identity and its requested room shell. The intent is the durable handle
	// a later cancellation or relocation resolves back to the committed
	// placements, so unlike a request ID it is part of the command itself.
	IntentID *string
	Room     *modelRoomShell
	// Adopted holds adopt_room's already-built native room: the inspected
	// rectangle and entrance side, and optionally the exact observed interior
	// and boundary door of a nonrectangular room. It reuses IntentID for the
	// adoption's own intent identity, which never coexists with build_room's.
	Adopted *modelAdoptedRoom
	// Goal holds create_goal's requested maintained goal kind (from a fixed
	// whitelist, so nothing to bound against facts) or cancel_goal's target
	// goal identity (which must be an observed one). The two commands are
	// separate contracts that never appear together, so they share the field.
	Goal *string
}

// modelRoomShell is an untrusted requested room shell: a rectangle, the wall
// and entrance definitions, one material and the entrance side. Every field is
// required, so pointers only distinguish absent from zero here; the domain
// constructor owns the range and distinctness rules.
type modelRoomShell struct {
	X        *int32  `json:"x"`
	Z        *int32  `json:"z"`
	Width    *int32  `json:"width"`
	Height   *int32  `json:"height"`
	WallDef  *string `json:"wallDef"`
	DoorDef  *string `json:"doorDef"`
	Material *string `json:"material"`
	Entrance *string `json:"entrance"`
	Purpose  *string `json:"purpose"`
}

// modelAdoptedRoom is an untrusted already-built room the model proposes the
// player adopt. The rectangle and entrance side are required; interiorCells and
// entranceCell are the optional nonrectangular half and must appear together,
// which the domain constructor enforces. Unlike modelRoomShell there is no
// wall, door or material definition: adoption builds nothing.
type modelAdoptedRoom struct {
	X             *int32      `json:"x"`
	Z             *int32      `json:"z"`
	Width         *int32      `json:"width"`
	Height        *int32      `json:"height"`
	Entrance      *string     `json:"entrance"`
	InteriorCells []modelCell `json:"interiorCells"`
	EntranceCell  *modelCell  `json:"entranceCell"`
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
	case "tend":
		return decodeTwoPawns(fields, "tend", "doctor", "patient")
	case "rescue":
		return decodeTwoPawns(fields, "rescue", "rescuer", "patient")
	case "draft":
		return decodeOnePawn(fields, "draft", "pawn")
	case "caravan":
		return decodeCaravan(fields)
	case "husbandry":
		return decodeHusbandry(fields)
	case "recover":
		return decodeRecoveryService(fields)
	case "bed_assign":
		return decodeBedAssign(fields)
	case "move_pawn":
		return decodeMove(fields)
	case "set_building_temperature":
		return decodeSetBuildingTemperature(fields)
	case "request_surgery":
		return decodeSurgery(fields)
	case "hold_caravan":
		return decodeHoldCaravan(fields)
	case "route_caravan":
		return decodeRouteCaravan(fields)
	case "accept_quest":
		return decodeAcceptQuest(fields)
	case "fulfill_quest":
		return decodeFulfillQuest(fields)
	case "gift_settlement":
		return decodeGiftSettlement(fields)
	case "create_zone":
		return decodeCreateZone(fields)
	case "edit_zone":
		return decodeEditZone(fields)
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
	case "build_room":
		return decodeBuildRoom(fields)
	case "adopt_room":
		return decodeAdoptRoom(fields)
	case "cancel_construction":
		return decodeCancelConstruction(fields)
	case "relocate_construction":
		return decodeRelocateConstruction(fields)
	case "evaluate_world":
		return decodeEvaluateWorld(fields)
	default:
		return modelCommand{}, fail(UnsupportedCommand, "only building, research, tend, rescue, draft, caravan, husbandry, recover, bed_assign, move_pawn, set_building_temperature, request_surgery, hold_caravan, route_caravan, accept_quest, fulfill_quest, gift_settlement, create_zone, edit_zone, build_room, adopt_room, cancel_construction, relocate_construction, set_population_policy, set_expedition_policy, set_population_decision, modify_resource_policy, set_resource_reserve, create_goal, cancel_goal and evaluate_world proposals are supported")
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

func decodeTwoPawns(fields map[string]json.RawMessage, command, firstKey, secondKey string) (modelCommand, error) {
	if len(fields) != 3 || fields[firstKey] == nil || fields[secondKey] == nil {
		return modelCommand{}, fail(InvalidCommand, "unexpected command fields")
	}
	decodeField := func(key string) (*string, error) {
		if bytes.Equal(bytes.TrimSpace(fields[key]), []byte("null")) {
			return nil, fail(InvalidCommand, "missing "+key)
		}
		var value string
		if err := json.Unmarshal(fields[key], &value); err != nil || value == "" {
			return nil, fail(InvalidCommand, "invalid "+key+" field")
		}
		return &value, nil
	}
	first, err := decodeField(firstKey)
	if err != nil {
		return modelCommand{}, err
	}
	second, err := decodeField(secondKey)
	if err != nil {
		return modelCommand{}, err
	}
	return modelCommand{Command: command, First: first, Second: second}, nil
}

func decodeOnePawn(fields map[string]json.RawMessage, command, key string) (modelCommand, error) {
	if len(fields) != 2 || fields[key] == nil || bytes.Equal(bytes.TrimSpace(fields[key]), []byte("null")) {
		return modelCommand{}, fail(InvalidCommand, "unexpected command fields")
	}
	var value string
	if err := json.Unmarshal(fields[key], &value); err != nil || value == "" {
		return modelCommand{}, fail(InvalidCommand, "invalid "+key+" field")
	}
	return modelCommand{Command: command, First: &value}, nil
}

func decodeCaravan(fields map[string]json.RawMessage) (modelCommand, error) {
	if len(fields) != 4 || fields["crew"] == nil || fields["cargo"] == nil || fields["destinationTile"] == nil {
		return modelCommand{}, fail(InvalidCommand, "unexpected command fields")
	}
	var crew []string
	if err := json.Unmarshal(fields["crew"], &crew); err != nil || len(crew) == 0 || len(crew) > 64 {
		return modelCommand{}, fail(InvalidCommand, "expected bounded nonempty crew list")
	}
	for _, pawn := range crew {
		if pawn == "" {
			return modelCommand{}, fail(InvalidCommand, "empty crew pawn")
		}
	}
	var rawCargo []json.RawMessage
	if err := json.Unmarshal(fields["cargo"], &rawCargo); err != nil || len(rawCargo) == 0 || len(rawCargo) > 256 {
		return modelCommand{}, fail(InvalidCommand, "expected bounded nonempty cargo list")
	}
	cargo := make([]modelCargo, 0, len(rawCargo))
	for _, raw := range rawCargo {
		var itemFields map[string]json.RawMessage
		if err := json.Unmarshal(raw, &itemFields); err != nil {
			return modelCommand{}, fail(InvalidCommand, "expected cargo object")
		}
		if len(itemFields) != 2 {
			return modelCommand{}, fail(InvalidCommand, "cargo requires two exact fields")
		}
		for _, key := range []string{"defName", "count"} {
			if itemFields[key] == nil || bytes.Equal(bytes.TrimSpace(itemFields[key]), []byte("null")) {
				return modelCommand{}, fail(InvalidCommand, "missing or null cargo field")
			}
		}
		var item modelCargo
		if err := json.Unmarshal(raw, &item); err != nil || *item.Definition == "" || *item.Count == 0 {
			return modelCommand{}, fail(InvalidCommand, "invalid cargo field")
		}
		cargo = append(cargo, item)
	}
	var tile int32
	if err := json.Unmarshal(fields["destinationTile"], &tile); err != nil || tile < 0 {
		return modelCommand{}, fail(InvalidCommand, "invalid destinationTile field")
	}
	return modelCommand{Command: "caravan", Crew: crew, Cargo: cargo, DestinationTile: &tile}, nil
}

func decodeHusbandry(fields map[string]json.RawMessage) (modelCommand, error) {
	if fields["animal"] == nil || fields["method"] == nil {
		return modelCommand{}, fail(InvalidCommand, "unexpected command fields")
	}
	var animal string
	if err := json.Unmarshal(fields["animal"], &animal); err != nil || animal == "" {
		return modelCommand{}, fail(InvalidCommand, "invalid animal field")
	}
	var method string
	if err := json.Unmarshal(fields["method"], &method); err != nil {
		return modelCommand{}, fail(InvalidCommand, "invalid method field")
	}
	switch method {
	case "train":
		if len(fields) != 4 || fields["trainableDef"] == nil || bytes.Equal(bytes.TrimSpace(fields["trainableDef"]), []byte("null")) {
			return modelCommand{}, fail(InvalidCommand, "unexpected command fields")
		}
		var def string
		if err := json.Unmarshal(fields["trainableDef"], &def); err != nil || def == "" {
			return modelCommand{}, fail(InvalidCommand, "invalid trainableDef field")
		}
		return modelCommand{Command: "husbandry", Animal: &animal, Method: &method, TrainableDef: &def}, nil
	case "slaughter":
		if len(fields) != 3 {
			return modelCommand{}, fail(InvalidCommand, "unexpected command fields")
		}
		return modelCommand{Command: "husbandry", Animal: &animal, Method: &method}, nil
	default:
		return modelCommand{}, fail(InvalidCommand, "invalid method field")
	}
}

func decodeRecoveryService(fields map[string]json.RawMessage) (modelCommand, error) {
	if len(fields) != 4 || fields["pawn"] == nil || fields["thing"] == nil || fields["method"] == nil {
		return modelCommand{}, fail(InvalidCommand, "unexpected command fields")
	}
	decodeField := func(key string) (*string, error) {
		if bytes.Equal(bytes.TrimSpace(fields[key]), []byte("null")) {
			return nil, fail(InvalidCommand, "missing "+key)
		}
		var value string
		if err := json.Unmarshal(fields[key], &value); err != nil || value == "" {
			return nil, fail(InvalidCommand, "invalid "+key+" field")
		}
		return &value, nil
	}
	pawn, err := decodeField("pawn")
	if err != nil {
		return modelCommand{}, err
	}
	thing, err := decodeField("thing")
	if err != nil {
		return modelCommand{}, err
	}
	method, err := decodeField("method")
	if err != nil {
		return modelCommand{}, err
	}
	switch *method {
	case "repair", "breakdown", "refuel":
	default:
		return modelCommand{}, fail(InvalidCommand, "invalid method field")
	}
	return modelCommand{Command: "recover", Pawn: pawn, Thing: thing, Service: method}, nil
}

func decodeBedAssign(fields map[string]json.RawMessage) (modelCommand, error) {
	if len(fields) != 3 || fields["pawn"] == nil || fields["bed"] == nil {
		return modelCommand{}, fail(InvalidCommand, "unexpected command fields")
	}
	decodeField := func(key string) (*string, error) {
		if bytes.Equal(bytes.TrimSpace(fields[key]), []byte("null")) {
			return nil, fail(InvalidCommand, "missing "+key)
		}
		var value string
		if err := json.Unmarshal(fields[key], &value); err != nil || value == "" {
			return nil, fail(InvalidCommand, "invalid "+key+" field")
		}
		return &value, nil
	}
	pawn, err := decodeField("pawn")
	if err != nil {
		return modelCommand{}, err
	}
	bed, err := decodeField("bed")
	if err != nil {
		return modelCommand{}, err
	}
	return modelCommand{Command: "bed_assign", Pawn: pawn, Bed: bed}, nil
}

func decodeMove(fields map[string]json.RawMessage) (modelCommand, error) {
	if len(fields) != 4 || fields["pawn"] == nil || fields["x"] == nil || fields["z"] == nil {
		return modelCommand{}, fail(InvalidCommand, "unexpected command fields")
	}
	if bytes.Equal(bytes.TrimSpace(fields["pawn"]), []byte("null")) {
		return modelCommand{}, fail(InvalidCommand, "missing pawn")
	}
	var pawn string
	if err := json.Unmarshal(fields["pawn"], &pawn); err != nil || pawn == "" {
		return modelCommand{}, fail(InvalidCommand, "invalid pawn field")
	}
	var x, z int32
	if err := json.Unmarshal(fields["x"], &x); err != nil || x < 0 {
		return modelCommand{}, fail(InvalidCommand, "invalid x field")
	}
	if err := json.Unmarshal(fields["z"], &z); err != nil || z < 0 {
		return modelCommand{}, fail(InvalidCommand, "invalid z field")
	}
	return modelCommand{Command: "move_pawn", Pawn: &pawn, X: &x, Z: &z}, nil
}

func decodeSetBuildingTemperature(fields map[string]json.RawMessage) (modelCommand, error) {
	if len(fields) != 3 || fields["thing"] == nil || fields["celsius"] == nil || bytes.Equal(bytes.TrimSpace(fields["thing"]), []byte("null")) || bytes.Equal(bytes.TrimSpace(fields["celsius"]), []byte("null")) {
		return modelCommand{}, fail(InvalidCommand, "unexpected command fields")
	}
	var thing string
	if err := json.Unmarshal(fields["thing"], &thing); err != nil || thing == "" {
		return modelCommand{}, fail(InvalidCommand, "invalid thing field")
	}
	var celsius float64
	if err := json.Unmarshal(fields["celsius"], &celsius); err != nil {
		return modelCommand{}, fail(InvalidCommand, "invalid celsius field")
	}
	return modelCommand{Command: "set_building_temperature", Thing: &thing, Celsius: &celsius}, nil
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

// decodeBuildRoom reads a whole requested room shell plus the player-chosen
// construction intent identity. The intent pattern is Python BuildRoom's own,
// and every shell field is required: this command replaces a rectangle's whole
// perimeter, so there is no half of it that could sensibly be left implied.
func decodeBuildRoom(fields map[string]json.RawMessage) (modelCommand, error) {
	if len(fields) != 3 || fields["intentId"] == nil || fields["room"] == nil {
		return modelCommand{}, fail(InvalidCommand, "unexpected command fields")
	}
	var intent string
	if err := json.Unmarshal(fields["intentId"], &intent); err != nil {
		return modelCommand{}, fail(InvalidCommand, "invalid intentId field")
	}
	if err := domain.ValidateRoomIntent(intent); err != nil {
		return modelCommand{}, &Failure{InvalidCommand, err}
	}
	var raw map[string]json.RawMessage
	if err := json.Unmarshal(fields["room"], &raw); err != nil || len(raw) != 9 {
		return modelCommand{}, fail(InvalidCommand, "unexpected or missing room fields")
	}
	for _, key := range []string{"x", "z", "width", "height", "wallDef", "doorDef", "material", "entrance", "purpose"} {
		value, ok := raw[key]
		if !ok || bytes.Equal(bytes.TrimSpace(value), []byte("null")) {
			return modelCommand{}, fail(InvalidCommand, "required room field missing or null")
		}
	}
	var room modelRoomShell
	if err := json.Unmarshal(fields["room"], &room); err != nil {
		return modelCommand{}, fail(InvalidCommand, "invalid room field")
	}
	return modelCommand{Command: "build_room", IntentID: &intent, Room: &room}, nil
}

// decodeAdoptRoom reads one already-built room the player is claiming, plus the
// adoption's own intent identity. The intent pattern is Python AdoptRoom's own,
// which is BuildRoom's.
//
// The rectangle and entrance side are required and the nonrectangular pair is
// optional, so unlike decodeBuildRoom this cannot demand a fixed field count.
// It accepts only the seven known keys and rejects an explicit null for any of
// them, then lets domain.NewRoomAdoption own every cross-field rule -- the
// both-or-neither pairing, the strictly-inside bound, the connectivity and the
// boundary entrance -- exactly as Python leaves them all to its `geometry`
// validator.
func decodeAdoptRoom(fields map[string]json.RawMessage) (modelCommand, error) {
	if len(fields) != 3 || fields["intentId"] == nil || fields["room"] == nil {
		return modelCommand{}, fail(InvalidCommand, "unexpected command fields")
	}
	var intent string
	if err := json.Unmarshal(fields["intentId"], &intent); err != nil {
		return modelCommand{}, fail(InvalidCommand, "invalid intentId field")
	}
	if err := domain.ValidateRoomIntent(intent); err != nil {
		return modelCommand{}, &Failure{InvalidCommand, err}
	}
	var raw map[string]json.RawMessage
	if err := json.Unmarshal(fields["room"], &raw); err != nil {
		return modelCommand{}, fail(InvalidCommand, "invalid room field")
	}
	known := map[string]bool{"x": true, "z": true, "width": true, "height": true, "entrance": true, "interiorCells": true, "entranceCell": true}
	for key, value := range raw {
		if !known[key] || bytes.Equal(bytes.TrimSpace(value), []byte("null")) {
			return modelCommand{}, fail(InvalidCommand, "unexpected or null room field")
		}
	}
	for _, key := range []string{"x", "z", "width", "height", "entrance"} {
		if _, ok := raw[key]; !ok {
			return modelCommand{}, fail(InvalidCommand, "required room field missing")
		}
	}
	var room modelAdoptedRoom
	if err := json.Unmarshal(fields["room"], &room); err != nil {
		return modelCommand{}, fail(InvalidCommand, "invalid room field")
	}
	for _, cell := range room.InteriorCells {
		if cell.X == nil || cell.Z == nil {
			return modelCommand{}, fail(InvalidCommand, "invalid adopted interior cell")
		}
	}
	if room.EntranceCell != nil && (room.EntranceCell.X == nil || room.EntranceCell.Z == nil) {
		return modelCommand{}, fail(InvalidCommand, "invalid adopted entrance cell")
	}
	return modelCommand{Command: "adopt_room", IntentID: &intent, Adopted: &room}, nil
}

// decodeCancelConstruction names one build_room intent and nothing else. There
// is deliberately no per-placement selector: the intent is the unit a player
// asked for and the unit Python's CancelConstruction withdraws, and letting a
// model name individual cells would invite it to cancel a cell it merely
// guessed at. Whether each placement is still pending is decided later against
// journalled progress, never here.
func decodeCancelConstruction(fields map[string]json.RawMessage) (modelCommand, error) {
	if len(fields) != 2 || fields["intentId"] == nil {
		return modelCommand{}, fail(InvalidCommand, "unexpected command fields")
	}
	var intent string
	if err := json.Unmarshal(fields["intentId"], &intent); err != nil {
		return modelCommand{}, fail(InvalidCommand, "invalid intentId field")
	}
	if err := domain.ValidateRoomIntent(intent); err != nil {
		return modelCommand{}, &Failure{InvalidCommand, err}
	}
	return modelCommand{Command: "cancel_construction", IntentID: &intent}, nil
}

// decodeRelocateConstruction names one already-submitted construction intent and
// the whole replacement shell to put in its place. It is decodeBuildRoom's shape
// over decodeCancelConstruction's target, which is exactly what the command is.
//
// Python's replacement is a RoomShell | Buildings union and its handler refuses a
// replacement of a different kind than the original. Here the field is a room
// shell and nothing else, because build_room is the only player command in this
// family that issues construction, so every relocatable construction is a room
// shell and the kind can never differ. There is deliberately no per-cell
// selector: a relocation moves the whole named room, exactly as a cancellation
// withdraws the whole named room.
func decodeRelocateConstruction(fields map[string]json.RawMessage) (modelCommand, error) {
	if len(fields) != 3 || fields["intentId"] == nil || fields["replacement"] == nil {
		return modelCommand{}, fail(InvalidCommand, "unexpected command fields")
	}
	var intent string
	if err := json.Unmarshal(fields["intentId"], &intent); err != nil {
		return modelCommand{}, fail(InvalidCommand, "invalid intentId field")
	}
	if err := domain.ValidateRoomIntent(intent); err != nil {
		return modelCommand{}, &Failure{InvalidCommand, err}
	}
	var raw map[string]json.RawMessage
	if err := json.Unmarshal(fields["replacement"], &raw); err != nil || len(raw) != 9 {
		return modelCommand{}, fail(InvalidCommand, "unexpected or missing replacement fields")
	}
	for _, key := range []string{"x", "z", "width", "height", "wallDef", "doorDef", "material", "entrance", "purpose"} {
		value, ok := raw[key]
		if !ok || bytes.Equal(bytes.TrimSpace(value), []byte("null")) {
			return modelCommand{}, fail(InvalidCommand, "required replacement field missing or null")
		}
	}
	var room modelRoomShell
	if err := json.Unmarshal(fields["replacement"], &room); err != nil {
		return modelCommand{}, fail(InvalidCommand, "invalid replacement field")
	}
	return modelCommand{Command: "relocate_construction", IntentID: &intent, Room: &room}, nil
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

func decodeSurgery(fields map[string]json.RawMessage) (modelCommand, error) {
	if len(fields) != 4 || fields["patient"] == nil || fields["recipe"] == nil || fields["part"] == nil {
		return modelCommand{}, fail(InvalidCommand, "unexpected command fields")
	}
	if bytes.Equal(bytes.TrimSpace(fields["patient"]), []byte("null")) {
		return modelCommand{}, fail(InvalidCommand, "missing patient")
	}
	var patient string
	if err := json.Unmarshal(fields["patient"], &patient); err != nil || patient == "" {
		return modelCommand{}, fail(InvalidCommand, "invalid patient field")
	}
	if bytes.Equal(bytes.TrimSpace(fields["recipe"]), []byte("null")) {
		return modelCommand{}, fail(InvalidCommand, "missing recipe")
	}
	var recipe string
	if err := json.Unmarshal(fields["recipe"], &recipe); err != nil || recipe == "" {
		return modelCommand{}, fail(InvalidCommand, "invalid recipe field")
	}
	if bytes.Equal(bytes.TrimSpace(fields["part"]), []byte("null")) {
		return modelCommand{}, fail(InvalidCommand, "missing part")
	}
	var part int32
	if err := json.Unmarshal(fields["part"], &part); err != nil || part < -1 {
		return modelCommand{}, fail(InvalidCommand, "invalid part field")
	}
	return modelCommand{Command: "request_surgery", Pawn: &patient, Recipe: &recipe, Part: &part}, nil
}

func decodeHoldCaravan(fields map[string]json.RawMessage) (modelCommand, error) {
	if len(fields) != 2 || fields["caravan"] == nil || bytes.Equal(bytes.TrimSpace(fields["caravan"]), []byte("null")) {
		return modelCommand{}, fail(InvalidCommand, "unexpected command fields")
	}
	var caravan string
	if err := json.Unmarshal(fields["caravan"], &caravan); err != nil || caravan == "" {
		return modelCommand{}, fail(InvalidCommand, "invalid caravan field")
	}
	return modelCommand{Command: "hold_caravan", Caravan: &caravan}, nil
}

func decodeRouteCaravan(fields map[string]json.RawMessage) (modelCommand, error) {
	if len(fields) != 5 || fields["caravan"] == nil || fields["destinationTile"] == nil || fields["returnHome"] == nil || fields["visitSettlement"] == nil {
		return modelCommand{}, fail(InvalidCommand, "unexpected command fields")
	}
	var caravan string
	if err := json.Unmarshal(fields["caravan"], &caravan); err != nil || caravan == "" {
		return modelCommand{}, fail(InvalidCommand, "invalid caravan field")
	}
	var returnHome, visitSettlement bool
	if err := json.Unmarshal(fields["returnHome"], &returnHome); err != nil {
		return modelCommand{}, fail(InvalidCommand, "invalid returnHome field")
	}
	if err := json.Unmarshal(fields["visitSettlement"], &visitSettlement); err != nil {
		return modelCommand{}, fail(InvalidCommand, "invalid visitSettlement field")
	}
	var tile *int32
	if !bytes.Equal(bytes.TrimSpace(fields["destinationTile"]), []byte("null")) {
		var t int32
		if err := json.Unmarshal(fields["destinationTile"], &t); err != nil || t < 0 {
			return modelCommand{}, fail(InvalidCommand, "invalid destinationTile field")
		}
		tile = &t
	}
	if returnHome == (tile != nil) {
		return modelCommand{}, fail(InvalidCommand, "choose exactly one of destinationTile or returnHome")
	}
	if returnHome && visitSettlement {
		return modelCommand{}, fail(InvalidCommand, "settlement visits require a route, not return-home")
	}
	return modelCommand{Command: "route_caravan", Caravan: &caravan, DestinationTile: tile, ReturnHome: &returnHome, VisitSettlement: &visitSettlement}, nil
}

func decodeCrew(raw json.RawMessage) ([]string, error) {
	var crew []string
	if err := json.Unmarshal(raw, &crew); err != nil || len(crew) == 0 || len(crew) > 64 {
		return nil, fail(InvalidCommand, "expected bounded nonempty crew list")
	}
	for _, pawn := range crew {
		if pawn == "" {
			return nil, fail(InvalidCommand, "empty crew pawn")
		}
	}
	return crew, nil
}

func decodeAcceptQuest(fields map[string]json.RawMessage) (modelCommand, error) {
	if len(fields) != 4 || fields["quest"] == nil || fields["accepterPawn"] == nil || fields["rewardChoice"] == nil {
		return modelCommand{}, fail(InvalidCommand, "unexpected command fields")
	}
	if bytes.Equal(bytes.TrimSpace(fields["quest"]), []byte("null")) {
		return modelCommand{}, fail(InvalidCommand, "missing quest")
	}
	var quest string
	if err := json.Unmarshal(fields["quest"], &quest); err != nil || quest == "" {
		return modelCommand{}, fail(InvalidCommand, "invalid quest field")
	}
	if bytes.Equal(bytes.TrimSpace(fields["accepterPawn"]), []byte("null")) {
		return modelCommand{}, fail(InvalidCommand, "missing accepterPawn")
	}
	var accepterPawn string
	if err := json.Unmarshal(fields["accepterPawn"], &accepterPawn); err != nil {
		return modelCommand{}, fail(InvalidCommand, "invalid accepterPawn field")
	}
	if bytes.Equal(bytes.TrimSpace(fields["rewardChoice"]), []byte("null")) {
		return modelCommand{}, fail(InvalidCommand, "missing rewardChoice")
	}
	var rewardChoice int32
	if err := json.Unmarshal(fields["rewardChoice"], &rewardChoice); err != nil || rewardChoice < -1 {
		return modelCommand{}, fail(InvalidCommand, "invalid rewardChoice field")
	}
	return modelCommand{Command: "accept_quest", Quest: &quest, AccepterPawn: &accepterPawn, RewardChoice: &rewardChoice}, nil
}

func decodeFulfillQuest(fields map[string]json.RawMessage) (modelCommand, error) {
	if len(fields) != 4 || fields["quest"] == nil || fields["caravan"] == nil || fields["crew"] == nil {
		return modelCommand{}, fail(InvalidCommand, "unexpected command fields")
	}
	if bytes.Equal(bytes.TrimSpace(fields["quest"]), []byte("null")) {
		return modelCommand{}, fail(InvalidCommand, "missing quest")
	}
	var quest string
	if err := json.Unmarshal(fields["quest"], &quest); err != nil || quest == "" {
		return modelCommand{}, fail(InvalidCommand, "invalid quest field")
	}
	if bytes.Equal(bytes.TrimSpace(fields["caravan"]), []byte("null")) {
		return modelCommand{}, fail(InvalidCommand, "missing caravan")
	}
	var caravan string
	if err := json.Unmarshal(fields["caravan"], &caravan); err != nil || caravan == "" {
		return modelCommand{}, fail(InvalidCommand, "invalid caravan field")
	}
	crew, err := decodeCrew(fields["crew"])
	if err != nil {
		return modelCommand{}, err
	}
	return modelCommand{Command: "fulfill_quest", Quest: &quest, Caravan: &caravan, Crew: crew}, nil
}

func decodeGiftSettlement(fields map[string]json.RawMessage) (modelCommand, error) {
	if len(fields) != 6 || fields["caravan"] == nil || fields["settlement"] == nil || fields["faction"] == nil || fields["crew"] == nil || fields["silver"] == nil {
		return modelCommand{}, fail(InvalidCommand, "unexpected command fields")
	}
	decodeField := func(key string) (*string, error) {
		if bytes.Equal(bytes.TrimSpace(fields[key]), []byte("null")) {
			return nil, fail(InvalidCommand, "missing "+key)
		}
		var value string
		if err := json.Unmarshal(fields[key], &value); err != nil || value == "" {
			return nil, fail(InvalidCommand, "invalid "+key+" field")
		}
		return &value, nil
	}
	caravan, err := decodeField("caravan")
	if err != nil {
		return modelCommand{}, err
	}
	settlement, err := decodeField("settlement")
	if err != nil {
		return modelCommand{}, err
	}
	faction, err := decodeField("faction")
	if err != nil {
		return modelCommand{}, err
	}
	crew, err := decodeCrew(fields["crew"])
	if err != nil {
		return modelCommand{}, err
	}
	if bytes.Equal(bytes.TrimSpace(fields["silver"]), []byte("null")) {
		return modelCommand{}, fail(InvalidCommand, "missing silver")
	}
	var silver int32
	if err := json.Unmarshal(fields["silver"], &silver); err != nil || silver <= 0 {
		return modelCommand{}, fail(InvalidCommand, "invalid silver field")
	}
	return modelCommand{Command: "gift_settlement", Caravan: caravan, Settlement: settlement, Faction: faction, Crew: crew, Silver: &silver}, nil
}

func decodeZoneCells(raw json.RawMessage) ([]modelCell, error) {
	var rawCells []json.RawMessage
	if err := json.Unmarshal(raw, &rawCells); err != nil || len(rawCells) == 0 || len(rawCells) > 256 {
		return nil, fail(InvalidCommand, "expected bounded nonempty cell list")
	}
	cells := make([]modelCell, 0, len(rawCells))
	for _, r := range rawCells {
		var fields map[string]json.RawMessage
		if err := json.Unmarshal(r, &fields); err != nil || len(fields) != 2 {
			return nil, fail(InvalidCommand, "expected cell object")
		}
		for _, key := range []string{"x", "z"} {
			if fields[key] == nil || bytes.Equal(bytes.TrimSpace(fields[key]), []byte("null")) {
				return nil, fail(InvalidCommand, "missing or null cell field")
			}
		}
		var cell modelCell
		if err := json.Unmarshal(r, &cell); err != nil {
			return nil, fail(InvalidCommand, "invalid cell field type")
		}
		cells = append(cells, cell)
	}
	return cells, nil
}

func decodeCreateZone(fields map[string]json.RawMessage) (modelCommand, error) {
	if fields["zoneKind"] == nil || fields["cells"] == nil {
		return modelCommand{}, fail(InvalidCommand, "unexpected command fields")
	}
	var kind string
	if err := json.Unmarshal(fields["zoneKind"], &kind); err != nil {
		return modelCommand{}, fail(InvalidCommand, "invalid zoneKind field")
	}
	cells, err := decodeZoneCells(fields["cells"])
	if err != nil {
		return modelCommand{}, err
	}
	switch kind {
	case "growing":
		if len(fields) != 4 || fields["crop"] == nil || bytes.Equal(bytes.TrimSpace(fields["crop"]), []byte("null")) {
			return modelCommand{}, fail(InvalidCommand, "unexpected command fields")
		}
		var crop string
		if err := json.Unmarshal(fields["crop"], &crop); err != nil || crop == "" {
			return modelCommand{}, fail(InvalidCommand, "invalid crop field")
		}
		return modelCommand{Command: "create_zone", ZoneKind: &kind, Crop: &crop, ZoneCells: cells}, nil
	case "stockpile":
		if fields["preset"] == nil || fields["priority"] == nil {
			return modelCommand{}, fail(InvalidCommand, "unexpected command fields")
		}
		var preset, priority string
		if err := json.Unmarshal(fields["preset"], &preset); err != nil {
			return modelCommand{}, fail(InvalidCommand, "invalid preset field")
		}
		if err := json.Unmarshal(fields["priority"], &priority); err != nil {
			return modelCommand{}, fail(InvalidCommand, "invalid priority field")
		}
		switch preset {
		case "food":
			if len(fields) != 5 {
				return modelCommand{}, fail(InvalidCommand, "unexpected command fields")
			}
			return modelCommand{Command: "create_zone", ZoneKind: &kind, Preset: &preset, Priority: &priority, ZoneCells: cells}, nil
		case "nothing":
			if len(fields) != 6 || fields["allow"] == nil {
				return modelCommand{}, fail(InvalidCommand, "unexpected command fields")
			}
			var allow []string
			if err := json.Unmarshal(fields["allow"], &allow); err != nil || len(allow) == 0 || len(allow) > 32 {
				return modelCommand{}, fail(InvalidCommand, "expected bounded nonempty allow list")
			}
			for _, name := range allow {
				if name == "" {
					return modelCommand{}, fail(InvalidCommand, "empty allow-list definition")
				}
			}
			return modelCommand{Command: "create_zone", ZoneKind: &kind, Preset: &preset, Priority: &priority, ZoneCells: cells, Allow: allow}, nil
		default:
			return modelCommand{}, fail(InvalidCommand, "invalid preset field")
		}
	default:
		return modelCommand{}, fail(InvalidCommand, "invalid zoneKind field")
	}
}

// decodeEditZone accepts only add, remove and delete operations. Crop and
// filter are declared domain.ZoneEditOp values with no constructor this
// slice (missing SettingsField evidence coverage), so any other requested
// operation string — including "crop" and "filter" — is rejected here as an
// unsupported operation, identically to an outright bogus value.
func decodeEditZone(fields map[string]json.RawMessage) (modelCommand, error) {
	if fields["zoneId"] == nil || fields["operation"] == nil || bytes.Equal(bytes.TrimSpace(fields["zoneId"]), []byte("null")) || bytes.Equal(bytes.TrimSpace(fields["operation"]), []byte("null")) {
		return modelCommand{}, fail(InvalidCommand, "unexpected command fields")
	}
	var zoneID string
	if err := json.Unmarshal(fields["zoneId"], &zoneID); err != nil || zoneID == "" {
		return modelCommand{}, fail(InvalidCommand, "invalid zoneId field")
	}
	var op string
	if err := json.Unmarshal(fields["operation"], &op); err != nil {
		return modelCommand{}, fail(InvalidCommand, "invalid operation field")
	}
	switch op {
	case "add", "remove":
		if len(fields) != 4 || fields["cells"] == nil {
			return modelCommand{}, fail(InvalidCommand, "unexpected command fields")
		}
		cells, err := decodeZoneCells(fields["cells"])
		if err != nil {
			return modelCommand{}, err
		}
		return modelCommand{Command: "edit_zone", ZoneID: &zoneID, ZoneOp: &op, ZoneCells: cells}, nil
	case "delete":
		if len(fields) != 3 {
			return modelCommand{}, fail(InvalidCommand, "unexpected command fields")
		}
		return modelCommand{Command: "edit_zone", ZoneID: &zoneID, ZoneOp: &op}, nil
	default:
		return modelCommand{}, fail(InvalidCommand, "unsupported zone edit operation")
	}
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
