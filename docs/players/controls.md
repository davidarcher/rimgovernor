# Launcher and controls

[Player guide](README.md)

The launcher controls the game and shows what the governor is doing.

| Tab | Use it to |
| --- | --- |
| Launch | Start, stop or restart the brain; select a save; generate a colony; change settings; read build and start diagnostics under Details. |
| Observe | Watch the running brain: Now, Concerns, Problems and Log. Its Resume and Pause buttons turn the bot on or off. |
| Acceptance | Run a native test case visibly on this checkout's private game. Stop the brain and close its game first. |

## Speed and manual control

**Play** enables automatic control on the loaded colony and after each load.
The default uses adaptive Ultrafast pacing. Pause in RimWorld to take manual
control; once Auto resumes, the governor can adjust work and settings again.

The Observe tab shows **Bot on** or **Bot off**. Resume turns the bot on and gives it the game clock.
Pause turns it off: the bot stops acting on the colony, the game is paused once if the bot was running it,
and you are free to unpause and run the game yourself.

## Watch the governor

The Observe tab reads the running game and needs nothing else installed. Its header shows
whether the brain answers, the game tick and pause state, and running counts of warnings and
errors. Play first; before the game is up the tab says so. Rows come from the brain's log, so
Problems and Log show what happened since the launcher began watching.

| Question | View |
| --- | --- |
| What stage is the colony in, is it healthy, how long do resources last, how is pace? | Now |
| What is each Concern doing, what is it blocked by, when is it reviewed next? | Concerns |
| What went wrong or was refused? Repeats are collapsed with a count. | Problems |
| What did the brain log? Filter by level, kind and text; click a row for its fields. | Log |

The tab refreshes every second and a half while it shows. The Concerns table is the latest
review, not a history.

Save before ending a session you want to keep. **Stop** ends the brain;
**Close game** ends its RimWorld process. See [save and resume](save-and-resume.md).
