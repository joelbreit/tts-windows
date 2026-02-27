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

## Usage

- Global hotkey: `Win + Alt + R`
- Tray menu:
  - `Read Selection`
  - `Show/Hide`
  - `Exit`
- Window controls:
  - Voice dropdown (prefers `(Natural)` voice by default when available)
  - Speed slider and `-0.1` / `+0.1` buttons (`0.1x` to `4.0x`)
  - `Read Selection`, `Pause`, `Resume`, `Stop`

## Persistence

- Settings file: `%AppData%\ReadSelectedTextTts\settings.json`
  - Selected voice ID
  - Playback speed
  - Hotkey modifiers/key
- Debug log: `%AppData%\ReadSelectedTextTts\debug.log`
  - Logs requested and applied playback rate

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
