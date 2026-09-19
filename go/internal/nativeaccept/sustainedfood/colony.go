package sustainedfood

import "fmt"

// sampleColony reads the service's live colony census (GET
// /api/player/colony: food nutrition and runway, colonists, downed, mood
// mean, the roster) for one timeline sample, so a sustained run's timeline
// carries the colony facts the run is judged by beside the goal state
// (#261). A failed read is recorded as an error block, never dropped.
func sampleColony(apiCall func(string, string, map[string]any, string) (map[string]any, int, error)) map[string]any {
	body, status, err := apiCall("GET", "/api/player/colony", nil, "")
	if err != nil {
		return map[string]any{"error": err.Error()}
	}
	if status != 200 {
		return map[string]any{"error": fmt.Sprintf("status %d: %v", status, body)}
	}
	return body
}

// ColonyOutcome summarizes the timeline's colony blocks so a sustained run
// can be judged from result.json alone: the worst food position seen, the
// colonist count at both ends, how many colonists the count lost along the
// way (every drop between consecutive readable samples, so a death and a
// departure both count), the worst downed count and mood mean, and how
// many samples had no readable census.
type ColonyOutcome struct {
	Samples             int      `json:"samples"`
	Unreadable          int      `json:"unreadable"`
	MinFoodRunwayDays   *float64 `json:"min_food_runway_days"`
	MinFoodNutrition    *float64 `json:"min_food_nutrition"`
	FinalFoodRunwayDays *float64 `json:"final_food_runway_days"`
	FirstColonists      *int     `json:"first_colonists"`
	FinalColonists      *int     `json:"final_colonists"`
	ColonistsLost       int      `json:"colonists_lost"`
	MaxDowned           int      `json:"max_downed"`
	FinalDowned         int      `json:"final_downed"`
	MinMoodMean         *float64 `json:"min_mood_mean"`
	FinalMoodMean       *float64 `json:"final_mood_mean"`
	// MinFoodRunwayTick is the live game tick of the sample that recorded
	// MinFoodRunwayDays, for finding the collapse in the flight recorder.
	MinFoodRunwayTick *uint64 `json:"min_food_runway_tick"`
}

// DeriveColonyOutcome folds every sample's colony block into a
// ColonyOutcome; it never errors, a timeline without colony blocks yields
// zero samples.
func DeriveColonyOutcome(timeline []map[string]any) ColonyOutcome {
	var out ColonyOutcome
	var lastColonists *int
	for _, sample := range timeline {
		colony, ok := sample["colony"].(map[string]any)
		if !ok {
			continue
		}
		out.Samples++
		if _, failed := colony["error"]; failed {
			out.Unreadable++
			continue
		}
		if runway, ok := asFloat(colony["foodRunwayDays"]); ok {
			if out.MinFoodRunwayDays == nil || runway < *out.MinFoodRunwayDays {
				out.MinFoodRunwayDays = &runway
				out.MinFoodRunwayTick = nil
				if tick, ok := sample["tick"].(uint64); ok {
					out.MinFoodRunwayTick = &tick
				}
			}
			out.FinalFoodRunwayDays = &runway
		}
		if nutrition, ok := asFloat(colony["foodNutrition"]); ok {
			if out.MinFoodNutrition == nil || nutrition < *out.MinFoodNutrition {
				out.MinFoodNutrition = &nutrition
			}
		}
		if count, ok := asFloat(colony["colonists"]); ok {
			colonists := int(count)
			if out.FirstColonists == nil {
				out.FirstColonists = &colonists
			}
			if lastColonists != nil && colonists < *lastColonists {
				out.ColonistsLost += *lastColonists - colonists
			}
			lastColonists = &colonists
			out.FinalColonists = &colonists
		}
		if downed, ok := asFloat(colony["downed"]); ok {
			out.FinalDowned = int(downed)
			if out.FinalDowned > out.MaxDowned {
				out.MaxDowned = out.FinalDowned
			}
		}
		if mood, ok := asFloat(colony["moodMean"]); ok {
			if out.MinMoodMean == nil || mood < *out.MinMoodMean {
				out.MinMoodMean = &mood
			}
			out.FinalMoodMean = &mood
		}
	}
	return out
}

// asFloat reads a decoded JSON number (float64) or a Go number a test
// supplied.
func asFloat(v any) (float64, bool) {
	switch n := v.(type) {
	case float64:
		return n, true
	case float32:
		return float64(n), true
	case int:
		return float64(n), true
	case int64:
		return float64(n), true
	case uint64:
		return float64(n), true
	}
	return 0, false
}
