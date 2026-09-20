package nativeaccept

import (
	"strings"
	"testing"
)

func TestTrimPrefsText(t *testing.T) {
	in := "<?xml version=\"1.0\"?>\n<PrefsData>\n  <autosaveIntervalDays>1</autosaveIntervalDays>\n  <temperatureMode>Celsius</temperatureMode>\n  <runInBackground>False</runInBackground>\n</PrefsData>"
	out, err := trimPrefsText(in)
	if err != nil {
		t.Fatal(err)
	}
	for _, want := range []string{
		"<autosaveIntervalDays>1000</autosaveIntervalDays>",
		"<adaptiveTrainingEnabled>False</adaptiveTrainingEnabled>",
		"<runInBackground>True</runInBackground>",
		"<temperatureMode>Celsius</temperatureMode>",
		"  <screenWidth>640</screenWidth>\n",
		"  <volumeMaster>0</volumeMaster>\n</PrefsData>",
	} {
		if !strings.Contains(out, want) {
			t.Errorf("missing %q in:\n%s", want, out)
		}
	}
	if strings.Count(out, "<runInBackground>") != 1 {
		t.Errorf("duplicated element:\n%s", out)
	}
	if _, err := trimPrefsText("<Other/>"); err == nil {
		t.Error("expected an error for a non-Prefs file")
	}
}

func TestSetPrefsTextReplacesOneElement(t *testing.T) {
	in := "<PrefsData>\n  <automaticPauseMode>MajorThreat</automaticPauseMode>\n</PrefsData>"
	out, err := setPrefsText(in, map[string]string{"automaticPauseMode": "Never"})
	if err != nil {
		t.Fatal(err)
	}
	if out != "<PrefsData>\n  <automaticPauseMode>Never</automaticPauseMode>\n</PrefsData>" {
		t.Errorf("unexpected rewrite:\n%s", out)
	}
}
