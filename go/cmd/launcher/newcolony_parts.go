package main

import (
	"strings"
	"unicode"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// The scenario composer's choice lists. Each is a curated set of Core defs
// that the page offers as suggestions; the inputs also take any other defName
// (a DLC or mod def), because native refuses an unknown name and lists the
// valid ones. Placement, context, faction and arrival values are the wire's
// lower-case enum names. The part rows themselves are httpapi's
// NewColonyPartsDTO, embedded in NewColonySpec, so the settings file, the page
// and the wire share one shape; the pawn count override is ColonistCount.

// TraitOption is one trait degree: Value is the TraitDef, Degree the
// TraitDegreeData degree (0 for a trait without degrees).
type TraitOption struct {
	Value  string `json:"value"`
	Degree int32  `json:"degree"`
	Label  string `json:"label"`
}

// ScenarioChoices are the lists the composer rows draw from.
type ScenarioChoices struct {
	Placements     []Option      `json:"placements"`
	Contexts       []Option      `json:"contexts"`
	Things         []Option      `json:"things"`
	Traits         []TraitOption `json:"traits"`
	Hediffs        []Option      `json:"hediffs"`
	Conditions     []Option      `json:"conditions"`
	Buildings      []Option      `json:"buildings"`
	Incidents      []Option      `json:"incidents"`
	PlayerFactions []Option      `json:"playerFactions"` // "" keeps the scenario's own
	ArrivalMethods []Option      `json:"arrivalMethods"` // "" keeps the scenario's own
}

// ScenarioPartDefaults seed a new row.
type ScenarioPartDefaults struct {
	Placement string  `json:"placement"`
	Count     uint32  `json:"count"`
	Chance    float32 `json:"chance"`
	Context   string  `json:"context"`
	Severity  float32 `json:"severity"`
}

var scenarioChoices = ScenarioChoices{
	Placements: []Option{
		{"start_with", "Start with (carried)", ""},
		{"start_near", "Start near (on the ground)", ""},
		{"scattered", "Scattered over the map", ""},
	},
	Contexts: []Option{
		{"player_starter", "Starting colonists", ""},
		{"all", "All pawns", ""},
		{"non_player", "Non-colonists", ""},
	},
	Things: thingOptions(policy.ScenarioThingChoices),
	Traits: []TraitOption{
		{"NaturalMood", 2, "Sanguine"},
		{"NaturalMood", 1, "Optimist"},
		{"NaturalMood", -1, "Pessimist"},
		{"NaturalMood", -2, "Depressive"},
		{"Immunity", 1, "Super-immune"},
		{"Immunity", -1, "Sickly"},
		{"SpeedOffset", 2, "Jogger"},
		{"SpeedOffset", 1, "Fast walker"},
		{"SpeedOffset", -1, "Slowpoke"},
		{"Nerves", 2, "Iron-willed"},
		{"Nerves", 1, "Steadfast"},
		{"Nerves", -1, "Nervous"},
		{"Nerves", -2, "Volatile"},
		{"Industriousness", 2, "Industrious"},
		{"Industriousness", 1, "Hard worker"},
		{"Industriousness", -1, "Lazy"},
		{"Industriousness", -2, "Slothful"},
		{"Neurotic", 1, "Neurotic"},
		{"Neurotic", 2, "Very neurotic"},
		{"ShootingAccuracy", 1, "Careful shooter"},
		{"ShootingAccuracy", -1, "Trigger-happy"},
		{"Beauty", 2, "Beautiful"},
		{"Beauty", -2, "Staggeringly ugly"},
	},
	Hediffs: []Option{
		{"Hypothermia", "Hypothermia", ""},
		{"Heatstroke", "Heatstroke", ""},
		{"Malnutrition", "Malnutrition", ""},
		{"Asthma", "Asthma", ""},
		{"Cataract", "Cataract", ""},
	},
	Conditions: []Option{
		{"ColdSnap", "Cold snap", ""},
		{"HeatWave", "Heat wave", ""},
		{"ToxicFallout", "Toxic fallout", ""},
		{"VolcanicWinter", "Volcanic winter", ""},
		{"Eclipse", "Eclipse", ""},
		{"Aurora", "Aurora", ""},
	},
	Incidents: []Option{
		{"RaidEnemy", "Enemy raid", ""},
		{"Infestation", "Infestation", ""},
		{"ManhunterPack", "Manhunter pack", ""},
		{"ShipChunkDrop", "Ship chunk drop", ""},
		{"ColdSnap", "Cold snap", ""},
		{"HeatWave", "Heat wave", ""},
		{"ToxicFallout", "Toxic fallout", ""},
		{"Eclipse", "Eclipse", ""},
		{"Disease_Flu", "Flu", ""},
		{"WandererJoin", "Wanderer joins", ""},
		{"RefugeePodCrash", "Refugee pod crash", ""},
		{"ResourcePodCrash", "Resource pod crash", ""},
		{"TraderCaravanArrival", "Trader caravan", ""},
		{"VisitorGroup", "Visitors", ""},
	},
	PlayerFactions: []Option{
		{"", "Scenario's own", ""},
		{"new_arrivals", "New arrivals", ""},
		{"new_tribe", "New tribe", ""},
	},
	ArrivalMethods: []Option{
		{"", "Scenario's own", ""},
		{"standing", "Standing", ""},
		{"drop_pods", "Drop pods", ""},
	},
}

// thingOptions labels each def by its name split at capitals and underscores.
func thingOptions(defs []string) []Option {
	out := make([]Option, len(defs))
	for i, d := range defs {
		var b strings.Builder
		for j, c := range d {
			switch {
			case c == '_':
				b.WriteByte(' ')
			case j > 0 && unicode.IsUpper(c):
				b.WriteString(" " + string(unicode.ToLower(c)))
			default:
				b.WriteRune(c)
			}
		}
		out[i] = Option{Value: d, Label: b.String()}
	}
	return out
}

var scenarioPartDefaults = ScenarioPartDefaults{Placement: "start_with", Count: 1, Chance: 1, Context: "player_starter", Severity: 1}
