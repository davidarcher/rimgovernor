# In-game goals panel

The goals window (#3497) lets the player see and edit goals inside the game. It is a Verse `Window` in the Bridge
assembly (`Bridge/Ui/GoalsWindow.cs`), not an ImGui window: the game drives ImGui as a rasterizer and feeds it no
input, so a mod-owned ImGui window would never receive clicks. It sits beside the launcher (see
[launcher](launcher.md)) and talks to the in-process brain only; there is no HTTP.

## Install

`NativeStartup.Install()` calls `GoalsToggle.Install()`, which patches `PlaySettings.DoPlaySettingsGlobalControls` with
a postfix that adds a toggle icon to the play-settings bar. It is skipped when `HeadlessRim.HeadlessMode.Active`, so the
acceptance build has no toggle. It is not a `[StaticConstructorOnStartup]`: those never run in the file-loaded Bridge.

## Threading and model

- `GoalsPanel` (`brain/src/RimGovernor.Brain.PlayerApi`) holds the state apart from drawing: the cached
  `GoalsViewModel`, the last edit's message and a queue of edits. `DoWindowContents` reads it and never awaits.
- `WindowUpdate` starts a refresh every 1.5 s. The refresh calls `BrainHost.GoalsView.Current()` on a pool thread and
  swaps in a new model. With no brain host the window shows "brain not started".
- `GoalsViewModel` is pure: the stored `GoalsRecord` plus the family's statuses, as table rows grouped by theme (goals
  with no theme first) and sorted by importance (high first), then id. A goal added since the last review shows
  "Pending". The table has no ETA column and no reordering; goal order is not part of the record.
- `GoalEdits` turn a form into a new `GoalsRecord` (add, edit, remove, preferences). An edit that fails validation
  returns the record it started from and the reason.
- `GoalsPanel.Submit` queues an edit. Edits apply in order, one `IGoalsEditor.SaveAsync` at a time, each to the record
  the previous one left, so an edit made during a save is never lost. A refresh never overwrites a model while an edit
  is pending. The host validates the whole record again; its refusal is shown in the window.

## Statuses

`GoalsRounds.ReadAsync` derives each goal's `GoalStatus` and each theme's `ThemeStatus` for its `goal_review` rows and
publishes them to the `GoalStatusBoard` (in memory, keyed to the colony). `HostGoals.Current()` returns them with the
record. Nothing is saved or sent over a wire.
