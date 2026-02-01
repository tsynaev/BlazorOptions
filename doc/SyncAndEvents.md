# Sync & Events (Universal)

This document describes the universal sync pipeline used by all modules (Positions, Trading History, Settings, etc.).

## Goals
- Server is the primary data source.
- Clients can work offline, queue changes as events, and sync later.
- All devices converge to the same state.
- Changes from one device are broadcast to others in real time.

## High?level architecture
- **Client outbox**: local append?only queue of events produced by UI actions.
- **SignalR hub**: bi?directional, real?time transport for sending/receiving events.
- **Server event stream**: append?only log per user (SQLite).
- **Conflict handling**: server deduplicates and applies events deterministically.
- **Rebuild**: clients can reconstruct local state by pulling server snapshots and/or pages.

## Event model
- Each change is represented as an `EventEnvelope`:
  - `EventId`: client?generated GUID (idempotency).
  - `DeviceId`: stable per device (local storage).
  - `OccurredUtc`: UTC timestamp of the event.
  - `Kind`: event type (e.g., `position.snapshot`, `trade.added`).
  - `Payload`: typed JSON payload defined in `BlazorOptions.Messages`.

## Client flow
1) User action updates local state immediately.
2) Client builds a typed event and writes it to the **outbox**.
3) SignalR connection sends pending events to the server.
4) Server acknowledges accepted `EventId`s.
5) Client removes acknowledged events from the outbox.

## Server flow
1) Server receives a batch of events.
2) Each event is inserted into the **event stream** (append?only).
3) If `EventId` already exists, the event is ignored (idempotent).
4) Server applies the event to the user’s persisted state.
5) Accepted events are broadcast to other devices in the same user group.

## Conflict resolution (examples)
- **Trades**: `trade.added` is deduplicated by `UniqueKey` (trade saved only once).
- **Positions**: `position.snapshot` is treated as “latest?wins” using `OccurredUtc`.

## Real?time updates
- When device A sends an event, the server publishes it to device B via SignalR.
- Device B applies the incoming event to its local state and updates the UI.

## Offline & recovery
- If offline, events remain queued in the outbox.
- On reconnect, events sync automatically.
- The client can rebuild local state by requesting server summaries/pages/snapshots.

## Current event types
- `position.snapshot`
- `trade.added`

## Extending to new modules
- Add a new DTO in `BlazorOptions.Messages`.
- Emit events from the client outbox on change.
- Apply event on server in `UserDataStore`.
- Broadcast to other devices via SignalR.
- Add local apply logic on the client.

