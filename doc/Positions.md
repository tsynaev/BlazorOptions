# Positions

The Positions feature lets you build, manage, and visualize option strategies and related legs.

## Core capabilities
- Create and manage multiple positions (portfolios) with custom names and colors.
- Add, remove, and reorder collections of legs within a position.
- Add legs manually or by parsing quick input (e.g., size/type/strike/expiry).
- Duplicate collections to iterate on variants.
- Load live positions from Bybit (options/linear/inverse) into a collection.
- Toggle leg inclusion and collection visibility to control what is shown in charts.
- Live price tracking and automatic leg price refresh from option chain data.
- Payoff chart updates for visible collections and selected valuation date.

## Persistence and sync
- Client persists the current positions locally for offline use.
- When signed in, position changes are sent as per-position snapshot events.
- When the Positions page opens, the client queues a local snapshot of added/updated positions and deleted position ids, then connects to sync.
- The server stores only the latest position snapshots and broadcasts updates to other devices via SignalR (no event stream storage).

## Typical workflow
1) Create a position or duplicate an existing collection.
2) Add legs using quick input or manual entry.
3) Visualize payoff and adjust settings (visibility, included legs).
4) Changes sync automatically when authenticated.
