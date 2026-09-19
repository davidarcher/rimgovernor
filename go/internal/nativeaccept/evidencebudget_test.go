package nativeaccept

import (
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func readEvidenceJSON(t *testing.T, path string) map[string]any {
	t.Helper()
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("read %s: %v", path, err)
	}
	var row map[string]any
	if err := json.Unmarshal(data, &row); err != nil {
		t.Fatalf("decode %s: %v", path, err)
	}
	return row
}

func bigEvidencePayload() json.RawMessage {
	return json.RawMessage(`{"cells":"` + strings.Repeat("x", EvidencePayloadCap) + `"}`)
}

func TestWriteEvidenceKeepsSmallPayloadsInline(t *testing.T) {
	dir := t.TempDir()
	writeEvidence(filepath.Join(dir, "0001-small.json"), map[string]any{"request": "r", "result": json.RawMessage(`{"ok":true}`)})
	row := readEvidenceJSON(t, filepath.Join(dir, "0001-small.json"))
	if _, truncated := row["truncated"]; truncated {
		t.Fatalf("small payload marked truncated: %v", row)
	}
	if result, _ := row["result"].(map[string]any); result["ok"] != true {
		t.Fatalf("payload not kept inline: %v", row)
	}
	if _, err := os.Stat(filepath.Join(dir, FullEvidenceDir)); !os.IsNotExist(err) {
		t.Fatalf("full dir created for a small payload: %v", err)
	}
}

func TestWriteEvidenceCapsLargePayloadsByDefault(t *testing.T) {
	t.Setenv(EvidenceEnv, "")
	if err := SetEvidenceMode(""); err != nil {
		t.Fatal(err)
	}
	dir := t.TempDir()
	payload := bigEvidencePayload()
	path := filepath.Join(dir, "0002-big.json")
	writeEvidence(path, map[string]any{"sequence": 2, "request": "r", "result": payload})
	row := readEvidenceJSON(t, path)
	if row["truncated"] != true {
		t.Fatalf("large payload not marked truncated: %v", keys(row))
	}
	if _, kept := row["result"]; kept {
		t.Fatal("large payload kept inline")
	}
	if row["sequence"] != float64(2) {
		t.Fatalf("stamps lost: %v", keys(row))
	}
	if got := int(row["result_bytes"].(float64)); got != len(payload) {
		t.Fatalf("result_bytes = %d, want %d", got, len(payload))
	}
	if preview, _ := row["result_preview"].(string); len(preview) != EvidencePayloadCap || !strings.HasPrefix(preview, `{"cells":"x`) {
		t.Fatalf("result_preview is not the first %d bytes (got %d)", EvidencePayloadCap, len(preview))
	}
	if sum, _ := row["result_sha256"].(string); len(sum) != 64 {
		t.Fatalf("result_sha256 = %q", sum)
	}
	if info, err := os.Stat(path); err != nil || info.Size() > int64(EvidencePayloadCap+1024) {
		t.Fatalf("capped row is %d bytes", info.Size())
	}
	if _, err := os.Stat(filepath.Join(dir, FullEvidenceDir, "0002-big.json")); !os.IsNotExist(err) {
		t.Fatalf("full payload written in capped mode: %v", err)
	}
}

func TestWriteEvidenceFullModeKeepsTheRow(t *testing.T) {
	if err := SetEvidenceMode(EvidenceFull); err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = SetEvidenceMode("") })
	dir := filepath.Join(t.TempDir(), "service")
	if err := os.MkdirAll(dir, 0755); err != nil {
		t.Fatal(err)
	}
	payload := bigEvidencePayload()
	writeEvidence(filepath.Join(dir, "http-0003.json"), map[string]any{"path": "/api/state", "response": payload})
	if row := readEvidenceJSON(t, filepath.Join(dir, "http-0003.json")); row["truncated"] != true || row["response_bytes"] == nil {
		t.Fatalf("capped row missing in full mode: %v", keys(row))
	}
	full := readEvidenceJSON(t, filepath.Join(dir, FullEvidenceDir, "http-0003.json"))
	if _, truncated := full["truncated"]; truncated {
		t.Fatal("full row marked truncated")
	}
	response, _ := full["response"].(map[string]any)
	if cells, _ := response["cells"].(string); len(cells) != EvidencePayloadCap {
		t.Fatalf("full row lost the payload: %v", keys(full))
	}
}

func TestEvidenceModeResolution(t *testing.T) {
	if err := SetEvidenceMode("verbose"); err == nil {
		t.Fatal("invalid mode accepted")
	}
	if err := SetEvidenceMode(""); err != nil {
		t.Fatal(err)
	}
	t.Setenv(EvidenceEnv, "full")
	if got := CurrentEvidenceMode(); got != EvidenceFull {
		t.Fatalf("env not honoured: %s", got)
	}
	t.Setenv(EvidenceEnv, "bogus")
	if got := CurrentEvidenceMode(); got != EvidenceCapped {
		t.Fatalf("invalid env not defaulted: %s", got)
	}
	if err := SetEvidenceMode(EvidenceCapped); err != nil {
		t.Fatal(err)
	}
	t.Setenv(EvidenceEnv, "full")
	if got := CurrentEvidenceMode(); got != EvidenceCapped {
		t.Fatalf("explicit mode not preferred over env: %s", got)
	}
	_ = SetEvidenceMode("")
}

func TestArtifactsHashesEvidenceAndReferencedFilesOnly(t *testing.T) {
	dir := t.TempDir()
	write := func(rel, body string) {
		path := filepath.Join(dir, filepath.FromSlash(rel))
		if err := os.MkdirAll(filepath.Dir(path), 0755); err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(path, []byte(body), 0644); err != nil {
			t.Fatal(err)
		}
	}
	write("0001-call.json", `{"a":1}`)
	write("service/http-0001.json", `{"b":2}`)
	write("full/0001-call.json", strings.Repeat("y", 100))
	write("flight.jsonl", strings.Repeat("z", 1000))
	write("service.sqlite", "db")
	write("service/stderr.log", "log")
	write("service-profile/Saves/checkpoint.rws", "save")
	report := Report{"checkpoint": "service-profile/Saves/checkpoint.rws", "nested": map[string]any{"log": filepath.Join(dir, "service", "stderr.log")}, "missing": "nope.txt"}
	hashes, err := Artifacts(dir, report)
	if err != nil {
		t.Fatal(err)
	}
	want := []string{"0001-call.json", "service/http-0001.json", "service-profile/Saves/checkpoint.rws", "service/stderr.log"}
	if len(hashes) != len(want) {
		t.Fatalf("hashed %v, want %v", hashes, want)
	}
	for _, w := range want {
		if hashes[w] == "" {
			t.Fatalf("%s not hashed: %v", w, hashes)
		}
	}
}
