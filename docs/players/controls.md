# Launcher and controls

[Player guide](README.md)

| Tab | Use it to |
| --- | --- |
| Launch | Start, stop or restart the controller; select a save; generate a colony; change settings. |
| Now | See the governor's current work, Concerns and blockers. |
| Acceptance | Run a native test case visibly on this checkout's private game. Stop the controller and close its game first. |
| Problems | Inspect problems and copy diagnostic rows. |
| Log | Read current-run events and build/start diagnostics. |

## Speed and manual control

**Play** enables automatic control on the loaded colony and after each load.
The default uses adaptive Ultrafast pacing. Pause in RimWorld to take manual
control; once Auto resumes, the governor can adjust work and settings again.

## Read the Now tab

The connection strip shows the colony, control state and freshness of the last
review. Below it:

- **Doing** identifies current work, the measurement it should change and its
  last progress.
- **Pursuing** shows the colony stage, prerequisites and Methods in progress.
- **Concerns** lists active needs, methods, status and review deadlines.
- **Waiting** explains unavailable methods, prerequisites and native blockers.

Under the report, **Why is the bot doing this?** lists every Concern (or
planner, such as "Tend the wounded and sick") that has changed state. Pick one
to see its history, newest first: what the bot found, what it was before and
how long that stood in game time, and the Method it had. A Method shown on a
"started" line may belong to the previous review. The history survives a
restart and is read-only.

A stale reading retains its last good value and is marked stale. Observe mode
shows its limited read-only status instead of an autonomous report.

Save before ending a session you want to keep. **Stop** ends the controller;
**Close game** ends its RimWorld process. See [save and resume](save-and-resume.md).
