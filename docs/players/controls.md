# Launcher and controls

[Player guide](README.md)

The launcher controls the game and shows what the governor is doing.

| Tab | Use it to |
| --- | --- |
| Launch | Start, stop or restart the brain; select a save; generate a colony; change settings; read build and start diagnostics under Details. |
| Observe | Watch the running brain: Now, Concerns, Problems and Log. Its Resume and Pause buttons turn the bot on or off. |
| Acceptance | Run a native test case visibly on this checkout's private game. Stop the brain and close its game first. |

Goals are set inside the game, not in the launcher: see [goals and themes](goals.md).

## Speed and manual control

**Play** enables automatic control on the loaded colony and after each load.
The default asks for Ultrafast, the game's own 15x. Hyperfast (30x, the game's tick boost held at that rate) is available
through the player API's speed route. A single native limit slows the game
when frames take too long or the brain stops responding; the brain only asks for a speed. The game pauses briefly
while the bot reviews a threat, handles a coupled order or saves (an autosave about once per in-game day, into
the rotating `Autosave-1..5` slots). To take manual control, press Pause in the launcher.

The Observe tab shows **Bot on** or **Bot off**. Resume turns the bot on and gives it the game clock.
Pause turns it off: the bot stops acting on the colony, the game is paused once if the bot was running it,
and you are free to unpause and run the game yourself.

While the bot is on, the game ignores your speed and pause input (space, 1, 2, 3, 4 and the time-control
buttons) and shows a message saying why. Press Pause in the launcher to take the clock back; the keys work
again as soon as the bot is off.

The clock in the bottom-right date readout shows the viewed tile's local time with seconds in place of the game's hour,
in the game's 12- or 24-hour form ("3:45:12 PM" or "15:45:12"). A game hour is 2,500 ticks, so the seconds
advance by one or two per tick.

## Watch the governor

Auto can account for learning while assigning useful cooking. A slower cook may
be worth using when the experience makes later needed meals cheaper to prepare;
urgent meals or a better alternative can favor the experienced cook instead.
There is no automatic Cooking target or reward for producing unwanted meals.
This currently uses qualified native cooking-speed and learning observations
within a short horizon. Missing mechanics remain visible limitations, and
predicted improvement is not proof that the pawn completed the work.

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

### Planned work and needs

In Auto, the governor compares supported food, sleep, recreation, building,
clothing and support work before assigning exact tasks. A short planned idle
interval means the next review is due within 600 game ticks or sooner when a
need reaches its observed threshold. Native timetable templates do not override
these choices.

Hunting is part of that comparison. A lone hunter is sent by designation. A
drafted hunting party (two or three shooters, sometimes a hauler) is chosen only
when the prediction worker is configured in the launcher's settings
(`RIMGOVERNOR_ENCOUNTER_WORKER`): each party is simulated in a separate process
before it is sent, ranked by the chance of a hunter dying or being seriously
hurt, then by the meat it brings home and the time it costs. The simulation's
estimates are coarse (sixteen samples per party) and a party is released as soon
as a shooter is downed, the prey changes or the scene no longer matches; without
the worker no party is ever formed.

A controlled colonist with no supported assignment stops supervised play with
an explicit unavailable-control reason. This can indicate unsupported needs or
missing facts/resources; it is not a claim that the colony has nothing to do.
Manual restores native voluntary behavior. Player orders and involuntary or
emergency states retain their existing protection.