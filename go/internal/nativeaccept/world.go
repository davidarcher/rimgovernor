package nativeaccept

import (
	"bytes"
	"crypto/rand"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"os"
	"strings"
)

// WorldRecord is a report's "world" block (#281): what decided the world a
// case ran on, so a failure can be read against the roll that produced it
// and a run reproduced (-seed). The installed build's hashes stay under
// package_files.
type WorldRecord struct {
	// Seed is the world seed: the one the debug start drew or was pinned
	// to, the scenario's, or the loaded save's <seedString>. Empty when
	// nothing knows it (a production build's quick start with no save).
	Seed string `json:"seed,omitempty"`
	// Pinned says the seed was given (DebugStart.Seed, `-seed`) rather
	// than drawn.
	Pinned bool `json:"pinned,omitempty"`
	// Save is the save the game loaded (a cached debug start, a Save
	// start, a resumed checkpoint) and SaveSHA256 the hash of its file
	// as it was loaded.
	Save       string `json:"save,omitempty"`
	SaveSHA256 string `json:"save_sha256,omitempty"`
	// Fixture is the start's fixture op and FixtureHash the hash over the
	// op and its arguments.
	Fixture     string `json:"fixture,omitempty"`
	FixtureHash string `json:"fixture_hash,omitempty"`
}

// worldSource is what a Start contributes to the record: the save it
// loaded (by profile name, "" when it generated or reused a world), the
// seed it knows and whether that seed was pinned.
type worldSource struct {
	save, seed string
	pinned     bool
}

// RecordWorld builds the record for start once it has loaded: the save's
// file is hashed and its seed string read, and a Fixture's op and
// arguments hashed. A save that cannot be read leaves the hash and seed
// to what the start knew; the record never fails a run.
func RecordWorld(cfg *Config, start Start) WorldRecord {
	src := start.world()
	rec := WorldRecord{Seed: src.seed, Pinned: src.pinned, Save: src.save}
	if src.save != "" && cfg != nil {
		if data, err := os.ReadFile(cfg.profileSave(src.save)); err == nil {
			sum := sha256.Sum256(data)
			rec.SaveSHA256 = hex.EncodeToString(sum[:])
			if seed := saveSeed(data); seed != "" {
				rec.Seed = seed
			}
		}
	}
	if f, ok := start.(Fixture); ok {
		rec.Fixture = f.Op
		rec.FixtureHash = FixtureHash(f.Op, f.Args)
	}
	return rec
}

// saveSeed is the <seedString> of a .rws, "" when it has none.
func saveSeed(data []byte) string {
	const open, close = "<seedString>", "</seedString>"
	i := bytes.Index(data, []byte(open))
	if i < 0 {
		return ""
	}
	rest := data[i+len(open):]
	j := bytes.Index(rest, []byte(close))
	if j < 0 {
		return ""
	}
	return strings.TrimSpace(string(rest[:j]))
}

// FixtureHash is the short hash over a fixture op and its arguments (their
// canonical JSON), so two runs' preconditions compare by one token.
func FixtureHash(op string, args map[string]any) string {
	data, err := json.Marshal(struct {
		Op   string         `json:"op"`
		Args map[string]any `json:"args,omitempty"`
	}{op, args})
	if err != nil {
		return ""
	}
	sum := sha256.Sum256(data)
	return hex.EncodeToString(sum[:8])
}

// RandomSeed draws a world seed for a debug start nothing pinned: ten
// lowercase letters, so it is a save-name-safe token the record and a
// later -seed can carry verbatim.
func RandomSeed() string {
	const letters = "abcdefghijklmnopqrstuvwxyz"
	var raw [10]byte
	if _, err := rand.Read(raw[:]); err != nil {
		return "unseeded"
	}
	out := make([]byte, len(raw))
	for i, b := range raw {
		out[i] = letters[int(b)%len(letters)]
	}
	return string(out)
}

// seedToken is seed as a save-name fragment: lowercase letters and
// digits only.
func seedToken(seed string) string {
	var out strings.Builder
	for _, r := range strings.ToLower(seed) {
		if (r >= 'a' && r <= 'z') || (r >= '0' && r <= '9') {
			out.WriteRune(r)
		}
	}
	return out.String()
}

// WorldOf reads a report's "world" block (a report decoded from JSON
// holds it as map[string]any).
func WorldOf(r map[string]any) (WorldRecord, bool) {
	switch block := r["world"].(type) {
	case WorldRecord:
		return block, true
	case map[string]any:
		pinned, _ := block["pinned"].(bool)
		return WorldRecord{
			Seed: AsString(block["seed"]), Pinned: pinned,
			Save: AsString(block["save"]), SaveSHA256: AsString(block["save_sha256"]),
			Fixture: AsString(block["fixture"]), FixtureHash: AsString(block["fixture_hash"]),
		}, true
	}
	return WorldRecord{}, false
}
