# Architecture reading guide

[Documentation](README.md)

Start with [How RimBot fits together](explanation/overview.md). The internals are
explained one topic at a time, with exact contracts in the
[reference](reference/README.md). Read [space and
resources](explanation/space-and-resources.md) for admission and dispatch constraints,
or [testing and evidence](explanation/testing.md) for what the available tests
establish. Unfinished work stays in [BACKLOG.md](BACKLOG.md).

The sections below are entry points for existing architecture links.

## Runtime and ownership

[Source map](reference/source-map.md).

## Observe, decide, execute, verify

[The control loop](explanation/control-loop.md).

## Interactive commands and shared intent

[Plans and Hands](explanation/plans-and-hands.md).

## Action and completion contracts

[Action completion contracts](reference/action-contracts.md).

## Player control and persistence

[Sessions, interruptions and recovery](explanation/sessions-and-recovery.md).

## Presentation, testing and extension

[Container and worker isolation](reference/container-isolation.md).

## Live interface delivery

[The dashboard and game view](explanation/dashboard.md).
