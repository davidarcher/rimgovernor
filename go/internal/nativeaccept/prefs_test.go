package nativeaccept

import (
	"testing"
)

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
