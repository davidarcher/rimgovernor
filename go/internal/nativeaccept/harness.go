package nativeaccept

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io/fs"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
)

// Harness owns evidence recording and generic native calls for the acceptance
// binaries: every native call/reply pair is written to <output>/NNNN-<label>.json
// for post-mortem review (evidence.go).
type Harness struct {
	Client *bridge.Client
	Output string
}

func NewHarness(client *bridge.Client, output string) *Harness {
	return &Harness{Client: client, Output: output}
}

// Call invokes a native tool by name with the given JSON-shaped arguments (nil means
// {}), records the request/reply pair as evidence, and returns the decoded structured
// reply as a generic JSON object.
func (h *Harness) Call(ctx context.Context, label, tool string, arguments any) (map[string]any, error) {
	// Every native call of a bridge-only case is a natural pause for the
	// checkpoint ring (#249); a capture in progress is not re-entered.
	checkpointPause(ctx)
	args, err := json.Marshal(arguments)
	if err != nil {
		return nil, fmt.Errorf("encode arguments for %s: %w", tool, err)
	}
	if arguments == nil {
		args = []byte("{}")
	}
	sequence := nextEvidenceSequence(h.Output)
	sent := time.Now()
	result, callErr := h.Client.NativeCall(ctx, tool, args)
	elapsed := time.Since(sent)
	if callErr != nil {
		writeEvidence(evidencePath(h.Output, sequence, label), evidenceRow(sequence, tool, args, sent, elapsed, result.Envelope, callErr, nil))
		return nil, callErr
	}
	var payload map[string]any
	if len(result.Structured) > 0 {
		if err := json.Unmarshal(result.Structured, &payload); err != nil {
			writeEvidence(evidencePath(h.Output, sequence, label), evidenceRow(sequence, tool, args, sent, elapsed, result.Envelope, nil, nil))
			return nil, fmt.Errorf("%s: structuredContent must be an object: %w", tool, err)
		}
	}
	var tick *uint64
	if observed, ok := replyTick(tool, payload); ok {
		tick = &observed
	}
	writeEvidence(evidencePath(h.Output, sequence, label), evidenceRow(sequence, tool, args, sent, elapsed, result.Envelope, nil, tick))
	observeReplyTick(tool, tick)
	if payload == nil {
		return map[string]any{}, nil
	}
	return payload, nil
}

// Wire calls a rimgovernor/* Protobuf-JSON tool: it wraps request as {"request":
// json.Marshal(request)} and unwraps the ProtoJSON string payload from the reply's
// "payload" field.
func (h *Harness) Wire(ctx context.Context, label, method string, request any) (map[string]any, error) {
	encoded, err := json.Marshal(request)
	if err != nil {
		return nil, fmt.Errorf("encode wire request for %s: %w", method, err)
	}
	reply, err := h.Call(ctx, label, "rimgovernor/"+method, map[string]any{"request": string(encoded)})
	if err != nil {
		return nil, err
	}
	payloadValue, ok := reply["payload"]
	payloadString, isString := payloadValue.(string)
	if !ok || !isString {
		return nil, fmt.Errorf("%s: reply did not preserve the ProtoJSON string envelope", method)
	}
	var message map[string]any
	if err := json.Unmarshal([]byte(payloadString), &message); err != nil {
		return nil, fmt.Errorf("%s: invalid ProtoJSON reply: %w", method, err)
	}
	return message, nil
}

// WireFunc adapts Wire to the map-typed WireFunc the authority helpers and
// ScenarioClock take.
func (h *Harness) WireFunc() WireFunc {
	return func(ctx context.Context, label, method string, request map[string]any) (map[string]any, error) {
		return h.Wire(ctx, label, method, request)
	}
}

// Outcome asserts the wire reply is exactly the single-field oneof named case and
// returns its value.
func Outcome(message map[string]any, cases ...string) (string, map[string]any, error) {
	if len(message) != 1 {
		return "", nil, fmt.Errorf("expected exactly one oneof case, found %v", keys(message))
	}
	for _, name := range cases {
		if value, ok := message[name]; ok {
			nested, ok := value.(map[string]any)
			if !ok {
				return "", nil, fmt.Errorf("oneof case %s must be an object", name)
			}
			return name, nested, nil
		}
	}
	return "", nil, fmt.Errorf("expected one of %v, found %v", cases, keys(message))
}

func keys(m map[string]any) []string {
	out := make([]string, 0, len(m))
	for k := range m {
		out = append(out, k)
	}
	sort.Strings(out)
	return out
}

// Discovery pages through games_tool_names and returns every discovered native tool
// name, bounded.
func (h *Harness) Discovery(ctx context.Context) ([]string, error) {
	var names []string
	cursor := ""
	seen := map[string]bool{}
	for page := 0; page < 100; page++ {
		if seen[cursor] {
			return nil, fmt.Errorf("discovery repeated a pagination cursor")
		}
		seen[cursor] = true
		result, err := h.Client.NativeNames(ctx, cursor, "")
		if err != nil {
			return nil, fmt.Errorf("games_tool_names: %w", err)
		}
		var page struct {
			Tools []struct {
				GABPName string `json:"gabpName"`
			} `json:"tools"`
			NextCursor string `json:"nextCursor"`
		}
		if err := json.Unmarshal(result.Structured, &page); err != nil {
			return nil, fmt.Errorf("invalid discovery page: %w", err)
		}
		for _, tool := range page.Tools {
			if tool.GABPName == "" {
				return nil, fmt.Errorf("discovery row missing its native gabpName")
			}
			names = append(names, tool.GABPName)
		}
		cursor = page.NextCursor
		if cursor == "" {
			return names, nil
		}
	}
	return nil, fmt.Errorf("discovery exceeded the bounded page limit")
}

// WaitForNativeTool polls games_tool_names until name is discoverable or timeout
// elapses. Some fixture ("test/*") tools are briefly absent from the native catalog
// immediately after a Superfast clock window stops (observed live: a games_call_tool
// on such a tool right after AdvanceGame returns can refuse with "Tool ... not found"
// even though the clock's own status already reports actualPaused/pauseVerified
// true), so callers that call a fixture tool right after AdvanceGame should wait for
// it here first rather than assume the catalog is already settled.
func WaitForNativeTool(ctx context.Context, client *bridge.Client, name string, timeout time.Duration) error {
	deadline := time.Now().Add(timeout)
	var lastErr error
	for {
		result, err := client.NativeNames(ctx, "", name)
		if err != nil {
			lastErr = err
		} else {
			var page struct {
				Tools []struct {
					GABPName string `json:"gabpName"`
				} `json:"tools"`
			}
			if err := json.Unmarshal(result.Structured, &page); err != nil {
				lastErr = fmt.Errorf("invalid discovery page: %w", err)
			} else {
				lastErr = nil
				for _, tool := range page.Tools {
					if tool.GABPName == name {
						return nil
					}
				}
			}
		}
		if time.Now().After(deadline) {
			if lastErr != nil {
				return fmt.Errorf("tool %q not discoverable after %s: %w", name, timeout, lastErr)
			}
			return fmt.Errorf("tool %q not discoverable after %s", name, timeout)
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(200 * time.Millisecond):
		}
	}
}

// ValidateDiscovery asserts discovered names carry no duplicate registrations, cover
// every required production export, cover every expectedFixture, and expose no
// fixture-shaped export (a "test/" prefix, a name containing "fixture", or a name in
// fixtures) beyond expectedFixtures.
func ValidateDiscovery(names []string, production, fixtures, expectedFixtures map[string]bool) error {
	counts := map[string]int{}
	for _, name := range names {
		counts[name]++
	}
	var duplicates []string
	for name, count := range counts {
		if count != 1 {
			duplicates = append(duplicates, name)
		}
	}
	if len(duplicates) > 0 {
		sort.Strings(duplicates)
		return fmt.Errorf("duplicate discovery registrations: %v", duplicates)
	}
	found := map[string]bool{}
	for _, name := range names {
		found[name] = true
	}
	var missingProduction []string
	for name := range production {
		if !found[name] {
			missingProduction = append(missingProduction, name)
		}
	}
	if len(missingProduction) > 0 {
		sort.Strings(missingProduction)
		return fmt.Errorf("missing production exports: %v", missingProduction)
	}
	var missingFixtures []string
	for name := range expectedFixtures {
		if !found[name] {
			missingFixtures = append(missingFixtures, name)
		}
	}
	if len(missingFixtures) > 0 {
		sort.Strings(missingFixtures)
		return fmt.Errorf("missing expected fixtures: %v", missingFixtures)
	}
	fixtureNames := map[string]bool{}
	for name := range found {
		if fixtures[name] || strings.HasPrefix(name, "test/") || strings.Contains(strings.ToLower(name), "fixture") {
			fixtureNames[name] = true
		}
	}
	var unexpected []string
	for name := range fixtureNames {
		if !expectedFixtures[name] {
			unexpected = append(unexpected, name)
		}
	}
	if len(unexpected) > 0 {
		sort.Strings(unexpected)
		return fmt.Errorf("unexpected fixture exports: %v", unexpected)
	}
	return nil
}

// PackageFiles hashes the unified native mod's on-disk files (used to record installed-artifact
// evidence in the report; it does not re-run RequireNativePackage's checks).
func PackageFiles(installation string) (map[string]string, error) {
	pkg := filepath.Join(installation, "Mods", "RimGovernor")
	files := []string{
		filepath.Join("About", "About.xml"),
		filepath.Join("Assemblies", "RimGovernor.Runtime.dll"),
		filepath.Join("BridgeTools", "RimGovernor", "RimGovernor.Bridge.dll"),
	}
	identity, err := packageID(filepath.Join(pkg, "About", "About.xml"))
	if err != nil {
		return nil, err
	}
	if identity != NativePackage {
		return nil, fmt.Errorf("installed package metadata does not identify the unified native mod")
	}
	hashes := map[string]string{}
	for _, name := range files {
		data, err := os.ReadFile(filepath.Join(pkg, name))
		if err != nil {
			return nil, fmt.Errorf("hash %s: %w", name, err)
		}
		sum := sha256.Sum256(data)
		hashes[name] = hex.EncodeToString(sum[:])
	}
	for _, old := range []string{"RimGovernorObservations", "RimGovernorHeadless"} {
		if _, err := os.Stat(filepath.Join(installation, "Mods", old)); err == nil {
			return nil, fmt.Errorf("mixed package installation: %s", old)
		}
	}
	return hashes, nil
}

// ArtifactHashes hashes every regular file under output, for the report's evidence
// manifest (an artifact census of every file under output).
func ArtifactHashes(output string) (map[string]string, error) {
	hashes := map[string]string{}
	err := filepath.WalkDir(output, func(path string, entry fs.DirEntry, err error) error {
		if err != nil {
			return err
		}
		if entry.IsDir() {
			return nil
		}
		data, err := os.ReadFile(path)
		if err != nil {
			return err
		}
		relative, err := filepath.Rel(output, path)
		if err != nil {
			return err
		}
		sum := sha256.Sum256(data)
		hashes[filepath.ToSlash(relative)] = hex.EncodeToString(sum[:])
		return nil
	})
	return hashes, err
}

// CheckStartupLog validates the mod's HeadlessRim startup markers: batch initialization must be
// active exactly when headless is requested, and no bootstrap/post-init error logged.
func CheckStartupLog(log string, headless bool) error {
	if strings.Contains(log, "[HeadlessRim] Bootstrap Error:") {
		return fmt.Errorf("headless bootstrap failed")
	}
	if strings.Contains(log, "[HeadlessRim] Post-Init Error:") {
		return fmt.Errorf("headless runtime patches failed")
	}
	active := strings.Contains(log, "[HeadlessRim] Headless mode active.")
	armed := strings.Contains(log, "[HeadlessRim] Bootstrap armed.")
	if active != headless || armed != headless {
		return fmt.Errorf("headless initialization disagrees with launch mode")
	}
	return nil
}
