package inputs

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestFixtureClassesNamesTheFixturesRegisteringTheOps(t *testing.T) {
	repo := t.TempDir()
	root := filepath.Join(repo, filepath.FromSlash(FixtureRoot))
	if err := os.MkdirAll(root, 0755); err != nil {
		t.Fatal(err)
	}
	for name, src := range map[string]string{
		"FarmEnvironmentFixture.cs":  `[Tool("test/farm_environment_prepare")] [Tool("test/farm_environment_observe")]`,
		"PowerFixture.cs":            `[Tool( "test/power_prepare")]`,
		"QuietStorytellerFixture.cs": `[Tool("test/quiet_storyteller")]`,
	} {
		if err := os.WriteFile(filepath.Join(root, name), []byte(src), 0644); err != nil {
			t.Fatal(err)
		}
	}
	got, err := FixtureClasses(repo, []string{"test/power_prepare", "test/farm_environment_prepare", "test/nobody_registers"})
	if err != nil {
		t.Fatal(err)
	}
	if want := []string{"FarmEnvironmentFixture", "PowerFixture"}; strings.Join(got, ",") != strings.Join(want, ",") {
		t.Fatalf("FixtureClasses = %v, want %v", got, want)
	}
	// A source the native project conditions on another flag names that
	// flag (ComfortFixture.cs builds under UpkeepFixture); two such sources
	// name it once.
	project := filepath.Join(repo, filepath.FromSlash(NativeProject))
	if err := os.MkdirAll(filepath.Dir(project), 0755); err != nil {
		t.Fatal(err)
	}
	csproj := `<Compile Include="../../../../scripts/fixtures/PowerFixture.cs" Condition="'$(PowerFixture)' == 'true'" />
<Compile Include="../../../../scripts/fixtures/FarmEnvironmentFixture.cs" Condition="'$(UpkeepFixture)' == 'true'" />
<Compile Include="../../../../scripts/fixtures/ComfortFixture.cs" Condition="'$(UpkeepFixture)' == 'true'" />`
	if err := os.WriteFile(project, []byte(csproj), 0644); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(root, "ComfortFixture.cs"), []byte(`[Tool("test/comfort_prepare")]`), 0644); err != nil {
		t.Fatal(err)
	}
	got, err = FixtureClasses(repo, []string{"test/power_prepare", "test/farm_environment_prepare", "test/comfort_prepare"})
	if err != nil {
		t.Fatal(err)
	}
	if want := []string{"PowerFixture", "UpkeepFixture"}; strings.Join(got, ",") != strings.Join(want, ",") {
		t.Fatalf("FixtureClasses with project flags = %v, want %v", got, want)
	}
	if got, err := FixtureClasses(repo, nil); err != nil || got != nil {
		t.Fatalf("no ops: %v %v", got, err)
	}
}
