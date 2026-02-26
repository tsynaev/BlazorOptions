# Block Trades

Route: `/block-trades`

## Purpose

- Shows Deribit ETH option trades from Amberdata.
- Selecting a block trade renders its payoff/PnL chart from detailed legs.
- Selected trade leg details are shown below the chart.
- Uses selectable start/end datetime range; default load window is last 24 hours.
- Start and end datetime are required and `start < end` must hold before loading.

## Data Source

- Uses Amberdata derivatives GraphQL:
- `BlockTrades` for the right list.
- `TopTradesByUniqueTrade` for selected trade leg details and chart legs.
- Trades are cached per selected datetime range to keep the page responsive.

## Layout

- Desktop:
  - left: payoff chart for selected trade
  - right: block trades list
- Mobile:
  - chart and list stack vertically
- Chart supports optional candle overlay toggle and always shows a current-price marker for selected trade context.
- Chart shows two markers: `Open Index` (from block trade) and `Futures` (editable).
- Estimated PnL is calculated from the current payoff curve at selected futures price and updates instantly when futures price changes (input or chart click).
- Estimated PnL also shows percent vs invested money for selected trade.

## List Fields

- uniqueTrade
- indexPrice
- tradeAmount
- netPremium
- numTrades

## Selected Trade Details

- Table below chart uses `TopTradesByUniqueTrade` rows.
- Chart is built from the same detailed legs to keep pricing consistent with the selected block trade.

## Selection

- Selected row is highlighted.
- Selection is persisted in query string: `?id=` (trade id/instrument).
