package stateval

import (
	"fmt"
	"slices"

	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	"google.golang.org/protobuf/proto"
)

// The data-driven StatParts (epic #2621): each reads only its own row's
// fields and what a definition request carries (the def, its stuff, the
// quality category), so the game's TransformValue is a pure function of the
// mirrored rows. Every part here keeps the default ForceShow (false).

// rowOf is the part's own row, or an error when the registry handed it the
// wrong message.
func rowOf[T proto.Message](row proto.Message, class string) (T, error) {
	typed, ok := row.(T)
	if !ok {
		var zero T
		return zero, fmt.Errorf("%s was given a %T row", class, row)
	}
	return typed, nil
}

// partHyperlinks is StatPart_Hyperlinks: TransformValue does nothing (it only
// contributes info-card hyperlinks).
type partHyperlinks struct{}

func (partHyperlinks) Class() string { return "StatPart_Hyperlinks" }

func (partHyperlinks) Transform(_ *Request, _ proto.Message, val float32) (float32, error) {
	return val, nil
}

func (partHyperlinks) ForceShow(*Request, proto.Message) (bool, error) { return false, nil }

// partQuality is StatPart_Quality: scale the value by the quality's factor,
// the gain capped at the quality's maxGain.
type partQuality struct{}

func (partQuality) Class() string { return "StatPart_Quality" }

func (partQuality) Transform(req *Request, row proto.Message, val float32) (float32, error) {
	p, err := rowOf[*d.StatPart_Quality](row, "StatPart_Quality")
	if err != nil {
		return 0, err
	}
	if val <= 0 && !p.GetApplyToNegativeValues() {
		return val, nil
	}
	var factor, maxGain float32
	switch req.Quality {
	case 0:
		factor, maxGain = p.GetFactorAwful(), p.GetMaxGainAwful()
	case 1:
		factor, maxGain = p.GetFactorPoor(), p.GetMaxGainPoor()
	case 2:
		factor, maxGain = p.GetFactorNormal(), p.GetMaxGainNormal()
	case 3:
		factor, maxGain = p.GetFactorGood(), p.GetMaxGainGood()
	case 4:
		factor, maxGain = p.GetFactorExcellent(), p.GetMaxGainExcellent()
	case 5:
		factor, maxGain = p.GetFactorMasterwork(), p.GetMaxGainMasterwork()
	case 6:
		factor, maxGain = p.GetFactorLegendary(), p.GetMaxGainLegendary()
	default:
		return 0, fmt.Errorf("quality %d is not a quality category", req.Quality)
	}
	gain := float32(float32(val*factor) - val)
	if maxGain < gain { // Mathf.Min
		gain = maxGain
	}
	return float32(val + gain), nil
}

func (partQuality) ForceShow(*Request, proto.Message) (bool, error) { return false, nil }

// partQualityOffset is StatPart_Quality_Offset: add the quality's offset when
// the part applies to the requested def (all defs when thingDefs is empty,
// which the game holds as a null list).
type partQualityOffset struct{}

func (partQualityOffset) Class() string { return "StatPart_Quality_Offset" }

func (partQualityOffset) Transform(req *Request, row proto.Message, val float32) (float32, error) {
	p, err := rowOf[*d.StatPart_Quality_Offset](row, "StatPart_Quality_Offset")
	if err != nil {
		return 0, err
	}
	// A definition request has no thing, so only req.Def can match; a
	// terrain def is never in a ThingDef list.
	if list := p.GetThingDefs(); len(list) > 0 && (req.Subject.Terrain || !slices.Contains(list, req.Subject.Def)) {
		return val, nil
	}
	var offset float32
	switch req.Quality {
	case 0:
		offset = p.GetOffsetAwful()
	case 1:
		offset = p.GetOffsetPoor()
	case 2:
		offset = p.GetOffsetNormal()
	case 3:
		offset = p.GetOffsetGood()
	case 4:
		offset = p.GetOffsetExcellent()
	case 5:
		offset = p.GetOffsetMasterwork()
	case 6:
		offset = p.GetOffsetLegendary()
	default:
		return 0, fmt.Errorf("quality %d is not a quality category", req.Quality)
	}
	return float32(val + offset), nil
}

func (partQualityOffset) ForceShow(*Request, proto.Message) (bool, error) { return false, nil }

// partStuff is StatPart_Stuff: add the stuff's power stat, scaled by the
// requested def's multiplier stat (both read without stuff or quality, as
// GetStatValueAbstract does).
type partStuff struct{}

func (partStuff) Class() string { return "StatPart_Stuff" }

func (partStuff) Transform(req *Request, row proto.Message, val float32) (float32, error) {
	p, err := rowOf[*d.StatPart_Stuff](row, "StatPart_Stuff")
	if err != nil {
		return 0, err
	}
	var power float32
	if req.Stuff != nil {
		if power, err = req.Evaluator.Value(p.GetStuffPowerStat(), ThingSubject(req.Stuff.GetDefName(), "")); err != nil {
			return 0, err
		}
	}
	multiplier, err := req.Evaluator.Value(p.GetMultiplierStat(), Subject{Def: req.Subject.Def, Terrain: req.Subject.Terrain})
	if err != nil {
		return 0, err
	}
	return float32(val + float32(multiplier*power)), nil
}

func (partStuff) ForceShow(*Request, proto.Message) (bool, error) { return false, nil }
