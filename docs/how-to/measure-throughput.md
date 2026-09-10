# Measure dashboard throughput

[Documentation](../README.md)

Sample cached dashboard state without taking control of the colony.

Run commands from the repository root. Keep generated evidence outside commits and
preserve failed results.

The Watch clock buttons request native time and enter Manual; Pause video affects only
the camera feed. Action follow uses native presentation on supported writes and defaults
off. Test these in a disposable rendered session; a read-only preview or mocked API test
does not establish game-level acceptance. Confirm unsaved chat, project and policy
drafts survive tab changes. Inspect Priorities and Work with blocked, active and
verified goals, and confirm raw IDs stay in diagnostics.

For a non-invasive throughput sample of an existing controller:

```powershell
.venv\Scripts\python.exe scripts/dashboard_throughput.py --port 8787 --seconds 120 --output .rimbot/throughput-sample
```

The output directory must be new. This sends only GET requests to cached dashboard
state; it does not change speed, enable rendering, dismiss interruptions or take
control. Wall TPS includes pauses. Paused time is a sampled approximation, and
stop-reason counts count samples rather than distinct incidents. It excludes intervals
across disconnects, rewinds and session changes. A paused colony yields zero TPS; peak
speed and safety require separate isolated gameplay acceptance.

## Related reading

[Choose tests](choose-tests.md) · [Test evidence explained](../explanation/testing.md) ·
[Backlog](../BACKLOG.md)
