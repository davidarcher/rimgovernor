package setup

import (
	"path/filepath"

	"golang.org/x/sys/windows/registry"
)

// steamInstallPath reads Steam's install directory from the registry: the
// user's SteamPath first, then the machine-wide InstallPath.
func steamInstallPath() (string, error) {
	for _, k := range []struct {
		root registry.Key
		path string
		name string
	}{
		{registry.CURRENT_USER, `Software\Valve\Steam`, "SteamPath"},
		{registry.LOCAL_MACHINE, `SOFTWARE\WOW6432Node\Valve\Steam`, "InstallPath"},
		{registry.LOCAL_MACHINE, `SOFTWARE\Valve\Steam`, "InstallPath"},
	} {
		key, err := registry.OpenKey(k.root, k.path, registry.QUERY_VALUE)
		if err != nil {
			continue
		}
		value, _, err := key.GetStringValue(k.name)
		key.Close()
		if err == nil && value != "" {
			return filepath.FromSlash(value), nil
		}
	}
	return "", nil
}
