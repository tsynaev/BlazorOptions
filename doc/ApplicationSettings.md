# Application Settings

The Application Settings feature centralizes user preferences and sync account access.

## Appearance
- Select Light, Dark, or System theme.
- Theme preference persists locally and follows system settings when chosen.

## Sync account
- Register or sign in to enable server sync across devices.
- Sign out to return to local-only mode.

## Notes
- When signed in, changes are synced to the server and broadcast to other devices.
- Without authentication, the app operates fully in local-only mode.

## Exchange Connections
- Exchange connections are managed from the Account Settings page.
- Supported connection ids are currently `bybit-main` and `bybit-demo`.
- Each connection stores its own API credentials, API base URL, public/private WebSocket URLs, live-price interval, and option base/quote coin lists.
- A missing supported connection can be added from Account Settings.
- Option instrument selection is driven by the selected connection's option base/quote coin lists.
