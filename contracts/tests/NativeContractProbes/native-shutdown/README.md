# Shutdown drain

Compiles the production `ShutdownDrain` without game assemblies. A final game-thread
request blocks stop completion until the production drain pumps it; disposal must
follow completion. Also checks no-brain cleanup and no disposal after a failed or
hung stop. The gate runs `native-shutdown`.

`lifecycle/shutdown-cleanup` supplies native evidence: actual engine callbacks stop
the brain and dispose the loaded map before the engine's allocation sweep. It
checks a duplicate notification and uses no simulation ticks.
