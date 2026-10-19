# Codex Switchboard Companion (preview)

This optional VS Code extension exposes two read-only commands:

- **Codex Switchboard: Show Current Status** — reads the active target and local profile counts.
- **Codex Switchboard: Show Cached Account Fleet** — displays saved ChatGPT accounts and quota data already present in Switchboard's local cache.

The extension starts the existing `codex-switchboard.exe` with fixed arguments and parses its JSON output. It does not access credentials, make network requests, or change the current route. It currently requires Windows and a locally built Switchboard CLI.

Set `codexSwitchboard.cliPath` to the executable's full path if it is not on `PATH`. To try the extension from this checkout, open this directory in VS Code and launch the extension development host (F5).
