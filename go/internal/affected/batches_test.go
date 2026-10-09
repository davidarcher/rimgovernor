package affected

import (
	"os"
	"path/filepath"
	"reflect"
	"strings"
	"testing"
)

func TestFileBatches(t *testing.T) {
	for _, tc := range []struct {
		name  string
		files []string
		want  int
	}{
		{"empty", nil, 0},
		{"exact boundary", []string{strings.Repeat("x", 3998), strings.Repeat("x", 3999)}, 1},
		{"over boundary", []string{strings.Repeat("x", 3999), strings.Repeat("x", 3999)}, 2},
		{"quoting and unicode", []string{strings.Repeat(`a b\"😀`, 600), strings.Repeat(`a b\"😀`, 600)}, 2},
	} {
		t.Run(tc.name, func(t *testing.T) {
			batches, err := fileBatches(tc.files)
			if err != nil {
				t.Fatal(err)
			}
			if len(batches) != tc.want {
				t.Fatalf("got %d batches, want %d", len(batches), tc.want)
			}
			var got []string
			for _, batch := range batches {
				size := 0
				for _, file := range batch {
					size += 2*len(file) + 3
				}
				if len(batch) == 0 || size > fileArgumentBudget {
					t.Fatalf("invalid batch size %d", size)
				}
				got = append(got, batch...)
			}
			if !reflect.DeepEqual(got, tc.files) {
				t.Fatal("batching lost or reordered arguments")
			}
		})
	}
	if _, err := fileBatches([]string{strings.Repeat("x", fileArgumentBudget)}); err == nil {
		t.Fatal("oversized argument accepted")
	}
}

// Exercise real gofmt processes with a list larger than Windows accepts in one
// command; an unformatted last file must remain visible across batch boundaries.
func TestGofmtWideFileList(t *testing.T) {
	dir := t.TempDir()
	file := filepath.Join(dir, "formatted file.go")
	if err := os.WriteFile(file, []byte("package fixture\n"), 0600); err != nil {
		t.Fatal(err)
	}
	var files []string
	for size := 0; size < 40000; size += len(file) + 3 {
		files = append(files, file)
	}
	bad := filepath.Join(dir, "last file.go")
	if err := os.WriteFile(bad, []byte("package fixture\nvar x=1\n"), 0600); err != nil {
		t.Fatal(err)
	}
	files = append(files, bad)
	batches, err := fileBatches(files)
	if err != nil {
		t.Fatal(err)
	}
	var result strings.Builder
	for _, batch := range batches {
		out, err := output(dir, "gofmt", append([]string{"-l"}, batch...)...)
		if err != nil {
			t.Fatal(err)
		}
		result.WriteString(out)
	}
	if got := strings.TrimSpace(result.String()); got != bad {
		t.Fatalf("gofmt output = %q, want %q", got, bad)
	}
}
