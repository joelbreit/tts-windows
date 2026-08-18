# ReadSelectedTextTts

Windows desktop app that reads currently selected text from the active app using local Windows voices or a cloud TTS provider.

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
- Global clipboard hotkey: `Win + Alt + C`
  - If unavailable on your system, the app falls back to `Ctrl + Alt + C`.
- Window behavior: a normal desktop window. Minimize keeps it in the taskbar and the
  global hotkeys keep working; closing the window exits the app.
- Window controls:
  - TTS provider dropdown + `⚙ Settings` button (see "TTS providers" below)
  - Voice dropdown (lists the active provider's voices)
  - Speed slider and `-0.1` / `+0.1` buttons (`0.1x` to `4.0x`)
  - `Read Selection`, `Read Clipboard`, `Read Test Text`, `Pause`, `Resume`, `Stop`
  - Built-in `Test Text` box for local playback verification without selecting text in another app
  - Status row at the bottom for notices (no selected text, hotkey fallback, read failures)

## TTS providers

The app supports multiple TTS providers behind a common abstraction
(`ITtsProvider` + `TtsProviderRegistry`). Built-in providers:

- **Windows (Local)** — offline SAPI voices; zero setup/cost.
- **Azure AI Speech (Neural)** — cloud neural voices (~300 voices); needs a Speech
  resource key + region. The F0 free tier covers 500k chars/month.
- **OpenAI TTS** — cloud voices via the OpenAI audio API; needs an OpenAI API key.
  Pay-as-you-go (no free tier). Optional `Model` field (`tts-1`, `tts-1-hd`,
  `gpt-4o-mini-tts`).

### Chunked playback (start-up latency)

Cloud providers synthesize a whole request before returning any audio, so a naive
one-request-per-read design makes the wait before the voice starts grow with the
length of the selection — a few thousand characters can mean several seconds of
silence. Instead, `TextChunker` splits a read at sentence boundaries and
`TtsService` plays it as a pipeline: the first chunk is deliberately short
(~180 chars, hard cap 400), so playback starts after **one small request**, while
later chunks (~900 chars) are synthesized in the background, three requests deep,
while earlier ones play. Time-to-first-audio is therefore roughly constant no
matter how long the selection is.

Consequences worth knowing:

- Chunk boundaries land on sentence ends, so the brief seam between chunks reads
  as a normal sentence pause. Text with no sentence punctuation is cut at a clause
  or word instead.
- A long read costs several API requests rather than one. Per-character pricing is
  unchanged; per-request quotas see more traffic.
- `SynthesisMs` in telemetry is time-to-first-audio, not total synthesis time.
- If a later chunk fails, playback stops there and the status row says which part
  it reached.

Enter cloud credentials under `⚙ Settings`. More providers can be added by
implementing `ITtsProvider` and registering it — the provider dropdown, Settings
UI, config storage, and telemetry pick it up automatically. See
[`docs/tts-options.html`](./docs/tts-options.html) for the provider shortlist.

Open `⚙ Settings` to:
- Browse providers with quality/latency/cost/free-tier info and pricing links
- Enter per-provider credentials (e.g. API keys) via dynamically-generated fields
- Set the **machine default** provider
- Review local **usage telemetry** (reads, characters, failures, avg time-to-first-audio)

API keys are encrypted at rest with **Windows DPAPI** (scoped to your user
account) — they are never written in plaintext. Usage telemetry is stored locally
only and never leaves the machine.

## A note on voices

The voice dropdown lists only the classic Windows SAPI voices (e.g. David, Mark,
Zira). The Windows 11 "Natural" / "Natural HD" voices (Ava, Aria, etc.) **cannot
be used by this app** — they are walled off from every public Windows TTS API and
reserved for Narrator. This is by design, not a bug, and installing them will not
make them appear. For the full investigation and better-voice alternatives (cloud
APIs and bundled local models), see
[`docs/windows-natural-voices-unavailable.md`](./docs/windows-natural-voices-unavailable.md)
and [`docs/tts-options.html`](./docs/tts-options.html).

## Persistence

- Settings file: `%AppData%\ReadSelectedTextTts\settings.json`
  - Selected provider + per-provider voice
  - Per-provider config (secrets DPAPI-encrypted; options in plaintext)
  - Playback speed
  - Hotkey modifiers/key
- Usage telemetry: `%AppData%\ReadSelectedTextTts\telemetry.jsonl`
  - One JSON line per read (provider, voice, char count, time-to-first-audio, chunk count, success)
  - Local only; clearable from the Settings window
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
7. Copy text to clipboard -> press `Win+Alt+C` -> app reads clipboard text without changing clipboard content.
8. Select several paragraphs with a cloud provider -> confirm the voice starts within
   about a second and that the read continues seamlessly across chunk boundaries.
9. During a long cloud read: `Pause` mid-sentence and `Resume` (audio continues from the
   same spot), then `Stop` (audio stops and no further chunks are requested).
