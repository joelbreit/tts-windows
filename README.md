# ReadSelectedTextTts

Windows tray app that reads currently selected text from the active app using local Windows voices.

## Requirements

- Windows 10/11
- .NET SDK 10.0+

## Build

```powershell
dotnet build ReadSelectedTextTts.slnx
```

## Run

```powershell
dotnet run --project .\ReadSelectedTextTts\ReadSelectedTextTts.csproj
```

### Optional: open a live log console

Run with `--console` to attach to the parent terminal (or open a new console if needed):

```powershell
dotnet run --project .\ReadSelectedTextTts\ReadSelectedTextTts.csproj -- --console
```

You can also enable console logging with an environment variable:

```powershell
$env:RSTTS_CONSOLE_LOG = "1"
dotnet run --project .\ReadSelectedTextTts\ReadSelectedTextTts.csproj
```

## Usage

- Global hotkey: `Win + Alt + R`
  - If unavailable on your system (common due OS shortcuts), the app falls back to `Ctrl + Alt + R`.
- Tray menu:
  - `Read Selection`
  - `Show/Hide`
  - `Exit`
- Window controls:
  - Voice dropdown (prefers `(Natural)` voice by default when available)
  - Speed slider and `-0.1` / `+0.1` buttons (`0.1x` to `4.0x`)
  - `Read Selection`, `Read Test Text`, `Pause`, `Resume`, `Stop`, `Exit`
  - Built-in `Test Text` box for local playback verification without selecting text in another app

## Persistence

- Settings file: `%AppData%\ReadSelectedTextTts\settings.json`
  - Selected voice ID
  - Playback speed
  - Hotkey modifiers/key
- App log: `%AppData%\ReadSelectedTextTts\app.log`
  - Includes playback events and temporary detailed selection diagnostics (UIA + clipboard fallback)

## Selection strategy

1. UI Automation (`TextPattern` / `TextPattern2`)
2. Clipboard-safe `Ctrl+C` fallback with clipboard restoration

## Manual test checklist

1. Select text in Notepad -> press `Win+Alt+R` -> app reads the selection.
2. Select text in Chrome address bar or webpage -> hotkey -> app reads it.
3. Select text in Word -> hotkey -> app reads it.
4. Change playback speed and confirm audible rate changes.
5. Disconnect network and confirm the app still works offline.
6. Copy text to clipboard, run `Read Selection`, then paste to confirm clipboard content is restored.
