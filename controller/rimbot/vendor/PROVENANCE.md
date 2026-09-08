# Companion UI reuse

`companion_ui.py` copies the pure `slim_surface` formatter and its helper
functions/constants from `instruments/ui.py` at Snowstar38/rimworld-claude-harness
revision `89c2e90fedd51419a3db55a7f9865b0aef29b270`.

The functions retain their upstream implementation. Imports, CLI, transport,
click matching, automatic dismissal and timers are excluded. The controller adds
a separate native controls list so exact captured IDs remain usable without
copying the upstream first-match text click behavior.

Reuse is for the user's authorized local research. The reviewed upstream has no
repository license file; this record does not grant redistribution rights.

Validation: pure formatting tests plus a rendered options-dialog capture and
native OK-button activation, with fresh window readback and pause preservation.
The observed capture shrank from 32,082 to 5,015 JSON characters. This does not
validate quest acceptance or text-field editing.
