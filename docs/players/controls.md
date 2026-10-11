# Launcher and controls

[Player guide](README.md)

The launcher controls the game; Grafana shows what the governor is doing.

| Tab | Use it to |
| --- | --- |
| Launch | Start, stop or restart the controller; select a save; generate a colony; change settings; read build and start diagnostics under Details; start the Observability stack and open its dashboards. |
| Acceptance | Run a native test case visibly on this checkout's private game. Stop the controller and close its game first. |

## Speed and manual control

**Play** enables automatic control on the loaded colony and after each load.
The default uses adaptive Ultrafast pacing. Pause in RimWorld to take manual
control; once Auto resumes, the governor can adjust work and settings again.

## Watch the governor in Grafana

The Now, Explain, Problems, Log and Ledger views live in Grafana, not in the launcher.
Turn on **Send telemetry to local collector** in Settings and start the
Observability stack (it needs Docker; see [launch](launch.md#settings)). When it
reads healthy, the Observability section shows a button for each dashboard.
While it is stopped the section shows a one-line hint.

| Question | Dashboard |
| --- | --- |
| What is the bot doing, what is it waiting on, what stage is the colony in? | Now |
| Why did a Concern change state, newest first, with the cause and the Method? | Concern transitions |
| What went wrong or was refused? | Problems |
| What did the controller log? Filter by level, kind and text. | Log |

Dashboards show the last 6 hours and refresh every 10 s. The concern history is
read-only and survives a restart. A method shown on a "started" line may belong
to the previous review.

Save before ending a session you want to keep. **Stop** ends the controller;
**Close game** ends its RimWorld process. See [save and resume](save-and-resume.md).
