package remoteaccept

import (
	"bytes"
	"encoding/json"
	"fmt"
	"image"
	"image/png"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestRenderedPNGExportAndImport(t *testing.T) {
	f := fixtureRun(t)
	job := exportFixture(t, f, 0)
	rel := f.attempts[0].Attempts[0].Case + "/frame.png"
	var encoded bytes.Buffer
	if err := png.Encode(&encoded, image.NewRGBA(image.Rect(0, 0, 2, 2))); err != nil {
		t.Fatal(err)
	}
	withTrailer := append(append([]byte{}, encoded.Bytes()...), []byte("private trailing payload")...)
	if err := os.WriteFile(filepath.Join(job.Output, rel), withTrailer, 0600); err != nil {
		t.Fatal(err)
	}
	if err := ExportShard(f.root, "s1", []ExportJob{job}, nil); err != nil {
		t.Fatal(err)
	}
	public := filepath.Join(f.root, "s1", "fixture", rel)
	data, err := os.ReadFile(public)
	if err != nil {
		t.Fatal(err)
	}
	if !bytes.Equal(data, encoded.Bytes()) {
		t.Fatal("PNG was not preserved as a canonical frame")
	}
	archive := filepath.Join(t.TempDir(), "evidence.zip")
	if err := os.WriteFile(archive, zipTree(t, f.root), 0600); err != nil {
		t.Fatal(err)
	}
	out := t.TempDir()
	if err := extract(archive, out); err != nil {
		t.Fatal(err)
	}
	if _, err := os.Stat(filepath.Join(out, "s1", "fixture", rel)); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(public, []byte("not a PNG"), 0600); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(archive, zipTree(t, f.root), 0600); err != nil {
		t.Fatal(err)
	}
	if err := extract(archive, t.TempDir()); err == nil {
		t.Fatal("disguised binary accepted as a rendered frame")
	}
}

func TestExportExecutableIdentityWithoutCopyingBinary(t *testing.T) {
	f := fixtureRun(t)
	wantDigest := strings.Repeat("a", 64)
	f.native(t, 0, 0, func(m map[string]json.RawMessage) {
		identity, err := json.Marshal(map[string]string{
			"executable": `C:\private\bin\rimgovernor.exe`, "sha256": wantDigest,
		})
		if err != nil {
			t.Fatal(err)
		}
		m["rimgovernor_binary"] = identity
	})
	f.save(t)
	job := exportFixture(t, f, 0)
	if err := ExportShard(f.root, "s1", []ExportJob{job}, nil); err != nil {
		t.Fatal(err)
	}
	var report struct {
		Binary struct {
			Executable string `json:"executable"`
			SHA256     string `json:"sha256"`
		} `json:"rimgovernor_binary"`
	}
	readTest(t, f.root, "s1/fixture/"+f.attempts[0].Attempts[0].Case+"/result.json", &report)
	if report.Binary.Executable != "[runner-path]" || report.Binary.SHA256 != wantDigest {
		t.Fatalf("binary identity was not retained and sanitized: %+v", report.Binary)
	}
	if _, err := os.Stat(filepath.Join(f.root, "s1", "attempts.json")); err != nil {
		t.Fatalf("complete attempts manifest missing: %v", err)
	}
}

func exportFixture(t *testing.T, f *fixture, index int) ExportJob {
	t.Helper()
	return exportFixtureVerdict(t, f, index, false)
}

func exportFixtureVerdict(t *testing.T, f *fixture, index int, fail bool) ExportJob {
	t.Helper()
	output := t.TempDir()
	rows := []map[string]any{}
	for _, a := range f.attempts[index].Attempts {
		var native map[string]json.RawMessage
		readTest(t, f.root, a.Evidence.Path, &native)
		if fail {
			native["passed"], native["exit"] = raw("false"), raw("1")
			a.Status, a.Classification = "failed", "assertion"
			a.Exit, a.Error = ptr(1), ptr("injected assertion failure")
		}
		a.Evidence = writeTest(t, output, a.Case+"/result.json", native)
		log := a.Case + ".log"
		if err := os.WriteFile(filepath.Join(output, log), []byte("test-token diagnostic at C:\\runner\\private\\game.exe\n"), 0600); err != nil {
			t.Fatal(err)
		}
		ref, err := FileRef(output, log)
		if err != nil {
			t.Fatal(err)
		}
		a.Log, _ = json.Marshal(ref)
		rows = append(rows, map[string]any{"name": a.Case, "attempts": []Attempt{a}, "output": filepath.Join(output, a.Case), "log": filepath.Join(output, log)})
	}
	writeTest(t, output, "result.json", map[string]any{"cases": rows})
	boot := filepath.Join(t.TempDir(), "bootstrap.json")
	b := []byte(`{"passed":true,"os":"windows","image_version":"fixture","cpu_count":4,"memory_bytes":16000000000,"free_disk_bytes":8000000000}`)
	if err := os.WriteFile(boot, b, 0600); err != nil {
		t.Fatal(err)
	}
	if err := os.MkdirAll(filepath.Join(output, "workers", "1"), 0700); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(output, "workers", "1", "private.json"), []byte(`{"secret":"AGE-SECRET-KEY-private"}`), 0600); err != nil {
		t.Fatal(err)
	}
	return ExportJob{Role: "fixture", Output: output, Bootstrap: boot}
}

func TestExportPortableEvidenceAndRedVerdict(t *testing.T) {
	for _, fail := range []bool{false, true} {
		t.Run(map[bool]string{false: "green", true: "red"}[fail], func(t *testing.T) {
			// Read immutable contract inputs directly and stage one case once.
			// Export writes the attempts manifest; no intermediate copy is needed.
			source := filepath.Join("..", "..", "..", "docs", "developers", "contracts", "remote-acceptance")
			f := &fixture{root: source, shards: []Shard{{ID: "s1", Status: "complete"}}, attempts: make([]Attempts, 1)}
			readTest(t, source, "run.json", &f.run)
			readTest(t, source, "selection.json", &f.selection)
			readTest(t, source, "s1/attempts.json", &f.attempts[0])
			f.selection.Cases = f.selection.Cases[:1]
			f.selection.Shards = f.selection.Shards[:1]
			f.selection.Shards[0].Cases = f.selection.Shards[0].Cases[:1]
			f.attempts[0].Attempts = f.attempts[0].Attempts[:1]
			job := exportFixtureVerdict(t, f, 0, fail)
			var bundle json.RawMessage
			readTest(t, source, f.run.Bundle.Path, &bundle)
			f.root = t.TempDir()
			f.run.Bundle = writeTest(t, f.root, f.run.Bundle.Path, bundle)
			r := writeTest(t, f.root, "run.json", f.run)
			f.selection.Run = r
			s := writeTest(t, f.root, "selection.json", f.selection)
			for i, sh := range f.shards {
				if err := ExportShard(f.root, sh.ID, []ExportJob{job}, []string{"test-token"}); err != nil {
					t.Fatal(err)
				}
				ref, err := FileRef(f.root, sh.ID+"/attempts.json")
				if err != nil {
					t.Fatal(err)
				}
				f.shards[i].Attempts = &ref
				b, err := os.ReadFile(filepath.Join(f.root, sh.ID, "fixture", f.attempts[i].Attempts[0].Case+".log"))
				if err != nil {
					t.Fatal(err)
				}
				if strings.Contains(string(b), "test-token") || strings.Contains(string(b), "private") {
					t.Fatalf("unsanitized log %s", b)
				}
				if _, err = os.Stat(filepath.Join(f.root, sh.ID, "fixture", "workers")); !os.IsNotExist(err) {
					t.Fatal("worker tree exported")
				}
			}
			e, err := Evaluate(f.root, r, s, f.shards)
			if err != nil {
				t.Fatal(err)
			}
			if e.Report.Passed == fail {
				t.Fatalf("incorrect verdict %+v", e.Aggregate)
			}
		})
	}
}

func TestExportWorkersCaseBesidePrivateRoots(t *testing.T) {
	f := fixtureRun(t)
	f.attempts[0].Attempts[0].Case = "workers/coverage"
	f.selection.Shards[0].Cases[0] = "workers/coverage"
	f.save(t)
	job := exportFixture(t, f, 0)
	for _, root := range []string{"workers/1", "workers/12"} {
		writeTest(t, job.Output, root+"/profile/private.json", map[string]string{"secret": "AGE-SECRET-KEY-private"})
	}
	if err := ExportShard(f.root, "s1", []ExportJob{job}, nil); err != nil {
		t.Fatal(err)
	}
	var attempts Attempts
	readTest(t, f.root, "s1/attempts.json", &attempts)
	want := "s1/fixture/workers/coverage/result.json"
	ref, err := FileRef(f.root, want)
	if err != nil {
		t.Fatal(err)
	}
	if attempts.Attempts[0].Evidence != ref {
		t.Fatalf("case evidence reference = %+v, want %+v", attempts.Attempts[0].Evidence, ref)
	}
	if _, err := os.Stat(filepath.Join(f.root, "s1/fixture/workers/coverage.log")); err != nil {
		t.Fatal(err)
	}
	for _, root := range []string{"workers/1", "workers/12"} {
		if _, err := os.Stat(filepath.Join(f.root, "s1/fixture", root)); !os.IsNotExist(err) {
			t.Fatalf("private worker root exported: %s (%v)", root, err)
		}
	}
}

func TestExportRejectsUnsafeOrIncompleteEvidence(t *testing.T) {
	for _, kind := range []string{"digest", "missing", "restricted", "budget", "extra"} {
		t.Run(kind, func(t *testing.T) {
			f := fixtureRun(t)
			job := exportFixture(t, f, 0)
			switch kind {
			case "digest":
				if err := os.WriteFile(filepath.Join(job.Output, f.attempts[0].Attempts[0].Case+"/result.json"), []byte(`{"passed":true}`), 0600); err != nil {
					t.Fatal(err)
				}
			case "missing":
				if err := os.Remove(filepath.Join(job.Output, f.attempts[0].Attempts[0].Case+".log")); err != nil {
					t.Fatal(err)
				}
			case "restricted":
				if err := os.WriteFile(filepath.Join(job.Output, "private.txt"), []byte("AGE-SECRET-KEY-abc"), 0600); err != nil {
					t.Fatal(err)
				}
			case "budget":
				f.run.Limits.Bytes = 64 << 20
				f.save(t)
			case "extra":
				var suite map[string]json.RawMessage
				readTest(t, job.Output, "result.json", &suite)
				suite["cases"] = raw(`[]`)
				writeTest(t, job.Output, "result.json", suite)
			}
			if err := ExportShard(f.root, "s1", []ExportJob{job}, nil); err == nil {
				t.Fatal("unsafe export succeeded")
			} else if kind == "missing" && !strings.Contains(err.Error(), f.attempts[0].Attempts[0].Case+".log") {
				t.Fatalf("refusal omitted the missing diagnostic path: %v", err)
			}
		})
	}
}

func TestExportFullShardBeyondFormerFifteenMiBAllowance(t *testing.T) {
	f := fixtureRun(t)
	job := exportFixture(t, f, 0)
	for len(f.selection.Shards) < 32 {
		f.selection.Shards = append(f.selection.Shards, PlannedShard{ID: fmt.Sprintf("s%d", len(f.selection.Shards)+1), Cases: []string{}})
	}
	f.run.Limits.Shards = 32
	rel := f.attempts[0].Attempts[0].Case + "/flight.jsonl"
	data := append([]byte(`{"message":"`), bytes.Repeat([]byte("x"), 16<<20)...)
	data = append(data, []byte("\"}\n")...)
	if err := os.WriteFile(filepath.Join(job.Output, rel), data, 0600); err != nil {
		t.Fatal(err)
	}
	f.save(t)
	if err := ExportShard(f.root, "s1", []ExportJob{job}, nil); err == nil || !strings.Contains(err.Error(), "allowance exhausted") {
		t.Fatalf("legacy explicit cap should reject the oversized shard: %v", err)
	}
	f.run.Limits.Bytes = 0
	f.save(t)
	if err := ExportShard(f.root, "s1", []ExportJob{job}, nil); err != nil {
		t.Fatal(err)
	}
	got, err := os.ReadFile(filepath.Join(f.root, "s1", "fixture", rel))
	if err != nil || !bytes.Equal(got, data) {
		t.Fatalf("diagnostic was lost or truncated: %v", err)
	}
}

func TestScheduledFullEvidence(t *testing.T) {
	f := fixtureRun(t)
	f.run.Tier = "full"
	f.run.Trigger.Event = "schedule"
	f.run.Trigger.PublishedRef = "refs/heads/main"
	for i := range f.selection.Cases {
		f.selection.Cases[i].Reasons = []string{"full"}
	}
	e, err := f.evaluate(t)
	if err != nil || !e.Report.Passed {
		t.Fatalf("%+v %v", e.Aggregate, err)
	}
}
