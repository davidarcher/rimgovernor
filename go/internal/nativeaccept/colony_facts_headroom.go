package nativeaccept

import (
	"context"
	"encoding/json"
	"fmt"
	"sort"
)

// ColonyFactsEnvelopeBytes is the native reply bound every
// observations_read_colony_facts answer must fit (ProtoBoundary
// MaximumEnvelopeBytes); a save whose routine review reads past it never
// scores a goal, so its clock never advances.
const ColonyFactsEnvelopeBytes = 1 << 20

// CommittedSaveHeadroomBytes is the most a committed save's planning colony
// facts may read: three quarters of the envelope, so one more building or
// stack of loot in a case that starts from it cannot tip the review over
// (issue #320: the facility startup checkpoint sat at 1,048,288 bytes).
const CommittedSaveHeadroomBytes = ColonyFactsEnvelopeBytes * 3 / 4

// ColonyFactsSize is one planning colony facts read: the reply's ProtoJSON
// byte count and the wire bytes of its largest sections (top-level, and
// under planning), largest first, for the report.
type ColonyFactsSize struct {
	Bytes    int
	Sections []SectionSize
}

// SectionSize is a section's name (dotted under its parent) and its bytes.
type SectionSize struct {
	Name  string
	Bytes int
}

// ColonyFactsPayloadSize reads the loaded game's colony facts exactly as
// the routine review does (the planning form, page limit 256) and returns
// its size, or the native unavailable/failure outcome as an error.
func ColonyFactsPayloadSize(ctx context.Context, h *Harness, label string) (ColonyFactsSize, error) {
	identity, err := ReadIdentity(ctx, h, label+"-identity")
	if err != nil {
		return ColonyFactsSize{}, err
	}
	reply, n, err := h.WireBytes(ctx, label, "observations_read_colony_facts", map[string]any{
		"scope": map[string]any{"expectedIdentity": identity}, "planning": true, "page": map[string]any{"limit": 256},
	})
	if err != nil {
		return ColonyFactsSize{}, err
	}
	_, observed, err := Outcome(reply, "observed")
	if err != nil {
		return ColonyFactsSize{}, fmt.Errorf("%s: %w", label, err)
	}
	size := ColonyFactsSize{Bytes: n}
	size.Sections = append(size.Sections, sectionSizes("", observed)...)
	section, _ := AsMap(observed["planning"])
	if planning, _ := AsMap(section["observed"]); planning != nil {
		size.Sections = append(size.Sections, sectionSizes("planning.", planning)...)
	}
	sort.SliceStable(size.Sections, func(i, j int) bool { return size.Sections[i].Bytes > size.Sections[j].Bytes })
	if len(size.Sections) > 8 {
		size.Sections = size.Sections[:8]
	}
	return size, nil
}

// sectionSizes is each field of m as the compact JSON bytes it re-encodes
// to (close to its ProtoJSON wire size), prefixed.
func sectionSizes(prefix string, m map[string]any) []SectionSize {
	var out []SectionSize
	for name, value := range m {
		encoded, err := json.Marshal(value)
		if err != nil {
			continue
		}
		out = append(out, SectionSize{Name: prefix + name, Bytes: len(encoded)})
	}
	return out
}

// BundleSize is one review step's bundle read (every census family a
// review step asks for, no clock status): the reply's ProtoJSON byte count
// and each family section's bytes, largest first, so the cost of a step's
// one native read is measurable per family (#360).
type BundleSize struct {
	Bytes    int
	Sections []SectionSize
}

// BundlePayloadSize reads the loaded game's review bundle exactly as a
// review step does (emergency, colony facts, population, research and the
// colonists' pawn detail; no clock status, which needs an owned clock) and
// returns its size. A family the native omitted (unreadable, or over the
// envelope) is reported at zero bytes.
func BundlePayloadSize(ctx context.Context, h *Harness, label string) (BundleSize, error) {
	identity, err := ReadIdentity(ctx, h, label+"-identity")
	if err != nil {
		return BundleSize{}, err
	}
	reply, n, err := h.WireBytes(ctx, label, "observations_read_bundle", map[string]any{
		"scope": map[string]any{"expectedIdentity": identity}, "emergency": true, "colonyFacts": true, "population": true, "research": true, "colonistPawns": true,
	})
	if err != nil {
		return BundleSize{}, err
	}
	_, observed, err := Outcome(reply, "observed")
	if err != nil {
		return BundleSize{}, fmt.Errorf("%s: %w", label, err)
	}
	size := BundleSize{Bytes: n}
	for _, name := range []string{"emergency", "colonyFacts", "population", "research", "colonistPawns"} {
		bytes := 0
		if value, ok := observed[name]; ok {
			if encoded, err := json.Marshal(value); err == nil {
				bytes = len(encoded)
			}
		}
		size.Sections = append(size.Sections, SectionSize{Name: name, Bytes: bytes})
	}
	// The colonists' pawn detail, summed per detail block over the pawns,
	// so the per-block cost of the routine detail flags is visible.
	if pawns, _ := AsMap(observed["colonistPawns"]); pawns != nil {
		blocks := map[string]int{}
		rows, _ := pawns["pawns"].([]any)
		for _, row := range rows {
			state, _ := AsMap(row)
			for name, value := range state {
				if encoded, err := json.Marshal(value); err == nil {
					blocks[name] += len(encoded)
				}
			}
		}
		for name, bytes := range blocks {
			size.Sections = append(size.Sections, SectionSize{Name: "colonistPawns." + name, Bytes: bytes})
		}
	}
	sort.SliceStable(size.Sections, func(i, j int) bool { return size.Sections[i].Bytes > size.Sections[j].Bytes })
	return size, nil
}

// CheckCommittedSaveHeadroom refuses to commit a save whose planning colony
// facts read past CommittedSaveHeadroomBytes and records the measured size
// under report["colony_facts"] either way, with the whole review bundle's
// per-family bytes under report["bundle"].
func CheckCommittedSaveHeadroom(ctx context.Context, h *Harness, report Report) error {
	size, err := ColonyFactsPayloadSize(ctx, h, "checkpoint-colony-facts")
	if err != nil {
		return fmt.Errorf("checkpoint colony facts: %w", err)
	}
	report["colony_facts"] = size
	bundle, err := BundlePayloadSize(ctx, h, "checkpoint-bundle")
	if err != nil {
		return fmt.Errorf("checkpoint bundle: %w", err)
	}
	report["bundle"] = bundle
	if size.Bytes > CommittedSaveHeadroomBytes {
		return fmt.Errorf("checkpoint colony facts read %d bytes, over the %d-byte committed-save headroom (envelope %d); largest sections %v", size.Bytes, CommittedSaveHeadroomBytes, ColonyFactsEnvelopeBytes, size.Sections)
	}
	return nil
}
