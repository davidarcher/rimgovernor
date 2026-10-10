package remoteaccept

import (
	"os"
	"path/filepath"
	"reflect"
	"strings"
	"testing"
)

func writeIndexFile(t *testing.T, root, rel, body string) {
	t.Helper()
	p := filepath.Join(root, filepath.FromSlash(rel))
	if err := os.MkdirAll(filepath.Dir(p), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(p, []byte(body), 0o644); err != nil {
		t.Fatal(err)
	}
}

func TestBuildIndexFailingFirstWithEvidencePaths(t *testing.T) {
	root := t.TempDir()
	writeIndexFile(t, root, "run.json", `{"trigger":{"actions_run_id":42,"actions_run_attempt":2}}`)
	writeIndexFile(t, root, "aggregate.json", `{"status":"failed","cases":[
		{"name":"a/ok","shard_id":"s1","attempt_count":1,"status":"passed"},
		{"name":"b/bad","shard_id":"s2","attempt_count":2,"status":"failed"},
		{"name":"c/lost","shard_id":"","attempt_count":0,"status":"missing"}]}`)
	writeIndexFile(t, root, "s1/fixture/a/ok/result.json", `{}`)
	writeIndexFile(t, root, "s2/fixture/b/bad/result.json", `{"diagnosis":{"sections":[
		{"name":"revision","note":"same"},{"name":"refusals","lines":[{"text":"no pawn","evidence":"x"},{"text":"bed|blocked","evidence":"y"}]}]}}`)
	writeIndexFile(t, root, "s2/fixture/b/bad/flight.jsonl", ``)
	writeIndexFile(t, root, "s2/fixture/b/bad/explain.jsonl", ``)
	writeIndexFile(t, root, "s2/fixture/b/bad.log", ``)
	writeIndexFile(t, root, "s2/fixture/snapshots/b/bad/routine-stream-9-2.jsonl", ``)
	writeIndexFile(t, root, "s2/fixture/snapshots/b/bad/routine-stream-1-1.jsonl", ``)

	x, err := BuildIndex(root)
	if err != nil {
		t.Fatal(err)
	}
	names := []string{}
	for _, c := range x.Cases {
		names = append(names, c.Name)
	}
	if !reflect.DeepEqual(names, []string{"b/bad", "c/lost", "a/ok"}) {
		t.Fatalf("order = %v", names)
	}
	bad := x.Cases[0]
	want := IndexCase{Name: "b/bad", Status: "failed", Attempts: 2, Shard: "s2", Artifact: "acceptance-42-2-shard-s2",
		Diagnosis: []string{"refusals: no pawn", "refusals: bed|blocked"},
		Report:    "s2/fixture/b/bad/result.json", Flight: "s2/fixture/b/bad/flight.jsonl", Explain: "s2/fixture/b/bad/explain.jsonl", Log: "s2/fixture/b/bad.log",
		Snapshots: []string{"s2/fixture/snapshots/b/bad/routine-stream-1-1.jsonl", "s2/fixture/snapshots/b/bad/routine-stream-9-2.jsonl"}}
	if !reflect.DeepEqual(bad, want) {
		t.Fatalf("bad = %+v\nwant %+v", bad, want)
	}
	if x.Cases[1].Artifact != "" || x.Cases[2].Flight != "" || x.Cases[2].Report == "" {
		t.Fatalf("rows = %+v", x.Cases)
	}
	md := x.Markdown()
	if !strings.Contains(md, "`acceptance-42-2-shard-s2`") || !strings.Contains(md, `bed\|blocked`) || strings.Index(md, "b/bad") > strings.Index(md, "a/ok") {
		t.Fatalf("markdown:\n%s", md)
	}
}

func TestBuildIndexNeedsAggregate(t *testing.T) {
	if _, err := BuildIndex(t.TempDir()); err == nil {
		t.Fatal("missing aggregate accepted")
	}
}
