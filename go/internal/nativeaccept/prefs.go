package nativeaccept

import (
	"fmt"
	"os"
	"regexp"
	"sort"
	"strings"
)

// HeadlessPrefs is what a headless profile's Prefs.xml is set to (issue
// #91): no autosaves in practice (a harness never wants the pause and disk
// write; the interval is game-days and must stay under ~35791 so that
// (int)(days * 60000f) does not overflow -- overflowed, the autosaver's
// threshold goes negative and it saves every tick, observed as a long
// event that never clears), the process kept
// running unfocused, the smallest window and no eye candy so a -batchmode
// player computes as little as possible, and a crash leaving ModsConfig.xml
// alone so the next launch still loads the mod. The learning helper
// (adaptiveTrainingEnabled) is off: its readout and concept popups are
// player UI a driven game never dismisses. Pause behaviour is left to
// the launch arguments and the harnesses (the letter/pause case sets and restores
// automaticPauseMode itself).
var HeadlessPrefs = map[string]string{
	"autosaveIntervalDays":    "1000",
	"runInBackground":         "True",
	"screenWidth":             "640",
	"screenHeight":            "480",
	"fullscreen":              "False",
	"uiScale":                 "1",
	"customCursorEnabled":     "False",
	"plantWindSway":           "False",
	"screenShakeIntensity":    "0",
	"textureCompression":      "True",
	"volumeMaster":            "0",
	"resetModsConfigOnCrash":  "False",
	"openLogOnWarnings":       "False",
	"pauseOnError":            "False",
	"adaptiveTrainingEnabled": "False",
}

// RenderedPrefs is what a rendered (windowed) profile's Prefs.xml is set to.
// RimWorld applies the saved resolution and fullscreen preference itself at
// startup (Prefs.Apply -> Screen.SetResolution), which overrides the
// -screen-fullscreen 0 / -screen-width / -screen-height launch arguments, so
// a player Prefs.xml saved full screen at desktop resolution came up full
// screen whatever the arguments said. Everything else stays the player's.
var RenderedPrefs = map[string]string{
	"screenWidth":  "1280",
	"screenHeight": "720",
	"fullscreen":   "False",
}

// TrimPrefs rewrites the Prefs.xml at path with HeadlessPrefs: each element
// present is replaced in place, each absent one is added before
// </PrefsData>. Anything else in the file is kept as the player set it.
func TrimPrefs(path string) error {
	return SetPrefs(path, HeadlessPrefs)
}

// SetPrefs rewrites the Prefs.xml at path with the given elements, the way
// TrimPrefs applies HeadlessPrefs. A harness that must play through letters
// the profile would pause on (a save whose threats arrive within the run)
// sets automaticPauseMode here before the game launches, since a paused
// major threat is a player interruption the controller never acknowledges
// on its own.
func SetPrefs(path string, prefs map[string]string) error {
	data, err := os.ReadFile(path)
	if err != nil {
		return err
	}
	text, err := setPrefsText(string(data), prefs)
	if err != nil {
		return fmt.Errorf("%s: %w", path, err)
	}
	return os.WriteFile(path, []byte(text), 0644)
}

func trimPrefsText(text string) (string, error) { return setPrefsText(text, HeadlessPrefs) }

func setPrefsText(text string, prefs map[string]string) (string, error) {
	const end = "</PrefsData>"
	if !strings.Contains(text, end) {
		return "", fmt.Errorf("not a Prefs.xml: no %s", end)
	}
	var missing []string
	for name, value := range prefs {
		pattern := regexp.MustCompile(`<` + name + `>[^<]*</` + name + `>`)
		replacement := "<" + name + ">" + value + "</" + name + ">"
		if pattern.MatchString(text) {
			text = pattern.ReplaceAllLiteralString(text, replacement)
		} else {
			missing = append(missing, "  "+replacement+"\n")
		}
	}
	// Deterministic order for the additions: the map walk is not.
	sort.Strings(missing)
	return strings.Replace(text, end, strings.Join(missing, "")+end, 1), nil
}
