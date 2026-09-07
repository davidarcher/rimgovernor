# Eight-member tribal benchmark

The baseline uses Lost Tribe, eight starting members, Cassandra / Adventure Story,
and a 250 by 250 map. Native supplies, research and scenario conditions apply.
The scenario description still contains its original five-person flavor text;
the native starting-pawn configuration and generated colony have eight members.

Create a baseline once with `.venv\Scripts\python.exe scripts/create_tribal_fixture.py --execute`.
This replaces the disposable loaded colony and requires the controller idle in Manual.
It pauses and saves `RimBot-tribal8-baseline` in RimWorld's Saves folder, verifies
its XML and tribal configuration, and records a SHA-256 in `.rimbot/fixtures`.
It refuses to overwrite an existing baseline. Saves and generated reports are not committed.

Reuse that exact save with `scripts/benchmark_setup.py --execute --save <absolute-rws-path>`.
The harness uses an isolated controller history with the dashboard model settings,
issues real game orders, and pauses on exit. The dashboard stays Manual to prevent
two controllers issuing orders. The world seed alone does not reproduce the pawns.

First acceptance check: eight usable sleeping arrangements, while observing actual
work, duplicate capacity and time to the first order. This narrow check is not proof
of a functioning starter base; roofed shelter, supply access, stockpile and sustained
food production require further observed checks.
