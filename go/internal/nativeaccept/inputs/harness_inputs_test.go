package inputs

import (
	"os"
	"path/filepath"
	"slices"
	"testing"
)

// FixtureInputs follows tool names to sources, class mentions between
// sources; build files come with every set.
func TestFixtureInputs(t *testing.T) {
	repo := t.TempDir()
	dir := filepath.Join(repo, filepath.FromSlash(FixtureRoot))
	if err := os.MkdirAll(dir, 0755); err != nil {
		t.Fatal(err)
	}
	write := func(name, body string) {
		if err := os.WriteFile(filepath.Join(dir, filepath.FromSlash(name)), []byte(body), 0644); err != nil {
			t.Fatal(err)
		}
	}
	write("Fixtures.csproj", "<Project/>")
	write("AFixture.cs", `[Tool("test/a_prepare")] void A() { BHelper.Do(); }`)
	write("BHelper.cs", `static class BHelper {}`)
	write("CFixture.cs", `[Tool("test/c_prepare")] void C() {}`)
	got, err := FixtureInputs(repo, FixtureRefs{"test/a_prepare": true, "test/": true})
	if err != nil {
		t.Fatal(err)
	}
	want := []string{
		"scripts/fixtures/AFixture.cs",
		"scripts/fixtures/BHelper.cs",
		"scripts/fixtures/Fixtures.csproj",
	}
	if !slices.Equal(got, want) {
		t.Errorf("FixtureInputs = %v, want %v", got, want)
	}
}

func TestWithoutOtherAreas(t *testing.T) {
	goDir := filepath.Join("repo", "go")
	cases := filepath.Join(goDir, "internal", "nativeaccept", "cases")
	dirs := []string{
		filepath.Join(cases, "light"),
		filepath.Join(cases, "defense"),
		filepath.Join(cases, "defense", "sub"),
		cases,
		filepath.Join(goDir, "internal", "nativeaccept"),
		filepath.Join(goDir, "internal", "nativeaccept", "casesx"),
	}
	got := WithoutOtherAreas(goDir, "defense", dirs)
	want := []string{
		filepath.Join(cases, "defense"),
		filepath.Join(cases, "defense", "sub"),
		cases,
		filepath.Join(goDir, "internal", "nativeaccept"),
		filepath.Join(goDir, "internal", "nativeaccept", "casesx"),
	}
	if !slices.Equal(got, want) {
		t.Errorf("WithoutOtherAreas = %v, want %v", got, want)
	}
}
