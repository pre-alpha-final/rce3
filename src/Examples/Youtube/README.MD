# Youtube controller

This Windows-only console app listens to an RCE3 feed and sends YouTube keyboard shortcuts to the active application. Keep the YouTube player focused while using it.

## Configuration

Set the feed URL through `RCE3_FEED`. For a protected feed, set the raw Authorization value through `RCE3_AUTH`:

```powershell
$env:RCE3_FEED = "http://localhost:5137/00000000-0000-0000-0000-000000000000"
$env:RCE3_AUTH = "replace-with-your-key"
dotnet run --project .\src\Examples\Youtube\Youtube\Youtube.csproj
```

You can instead pass the feed URL and optional authorization value as arguments. Arguments override their corresponding environment variables. Pass an empty second argument to connect to an open feed even when `RCE3_AUTH` is set:

```powershell
dotnet run --project .\src\Examples\Youtube\Youtube\Youtube.csproj -- "http://localhost:5137/00000000-0000-0000-0000-000000000000" ""
```

The app saves its reader GUID in a `readerid` file beside the executable and reuses it on restart without resetting the queue, then listens until you press Ctrl+C. It never publishes messages. Keep this file during updates; do not copy it to another installation. An invalid or unwritable identity file stops startup with an error. See the root README for the shared reader identity rule.

## Commands

Command bodies are exact and case-sensitive:

| Message | YouTube action |
| --- | --- |
| `youtube: back6` | Press J six times (back one minute) |
| `youtube: back` | Press J once (back ten seconds) |
| `youtube: playpause` | Press K once (play or pause) |
| `youtube: forward` | Press L once (forward ten seconds) |
| `youtube: forward6` | Press L six times (forward one minute) |

Unknown messages are ignored. The app does not trim whitespace or line endings from message bodies.
