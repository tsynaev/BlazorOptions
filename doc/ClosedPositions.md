# Closed Positions

## Overview
Closed positions store which symbols belong to the position and which start date should be used for each symbol.
The trade summary table shown above the symbol list is calculated outside this panel from the saved symbols and dates.

## Row behavior
- Each row stores a symbol and an optional `Since` value.
- If `Since` is empty, the app uses the position creation time as the default start date for that symbol.
- This panel only manages the saved symbol/date selection for the position.

## Selecting from trading history
Use **Select trades** to open the trading history picker. The dialog filters out symbols already in closed positions and lets you add new symbols from the grid.

## Position leg sync
- When you add an existing exchange-backed leg to a position, its symbol is added to closed positions automatically.
- When you remove a leg whose symbol is already tracked in closed positions, the app asks whether to remove that symbol from closed positions too.
