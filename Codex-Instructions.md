## Codex Instructions: “Read Selected Text Anywhere” (WPF)

> **⚠️ CORRECTION (2026-06-08): The "Natural" / "Neural" voice premise below is wrong.**
> This spec was written assuming the app could enumerate and use the installed
> Windows 11 "(Natural)" voices. It cannot — those voices are walled off from every
> public Windows TTS API and reserved for Narrator. `SpeechSynthesizer.AllVoices`
> only ever returns the legacy SAPI voices (David, Mark, Zira). The Natural-voice
> preference logic has been removed from the code. References to "(Natural)" /
> "Neural" voices in the sections below are retained only as historical record —
> **do not act on them.** See
> [`docs/windows-natural-voices-unavailable.md`](./docs/windows-natural-voices-unavailable.md)
> for the full investigation and [`docs/tts-options.html`](./docs/tts-options.html)
> for better-voice alternatives.

### Goal

Build a Windows desktop app for **personal use** that:

* Runs in the **system tray**
* Reads **currently selected text** from whatever app is focused
* Uses **installed local Windows voices** (classic SAPI voices — see the correction note above; "(Natural)" voices are not reachable)
* Can be triggered by:

  1. a **universal keyboard shortcut**
  2. a **right-click menu option** (best-effort; see constraints)
* Provides playback speed control:

  * **0.1x → 4.0x**
  * Adjust in **0.1x** increments
* Has basic playback controls: **Play / Pause / Stop / Resume** and **voice selection**

---

## Important Constraints (Codex: follow these)

### About “right-click menu option”

A true “right-click selected text in *any app*” context menu is **not generally possible** without one of:

* per-app integration,
* OS-level accessibility hooks beyond typical desktop apps,
* or writing an invasive system-wide shell/COM extension + text-selection plumbing that still won’t work in many apps.

✅ Implement a practical substitute:

* **Tray icon context menu** item: “Read Selection”
* Optional: **Explorer context menu for text files** (“Read Aloud”) as a separate nice-to-have (not required)

Primary UX should be **global hotkey**, which *does* meet the “anywhere selection” requirement.

---

## Technical Plan (Architecture)

### Project

* **.NET 10 WPF** app (Windows only)
* Single solution:

  * `ReadSelectedTextTts` (WPF)

### Core Components

1. **Selection Acquisition**

   * Primary: **UI Automation** (UIA) `TextPattern` / `TextPattern2`
   * Fallback: **Clipboard-safe Ctrl+C injection**

     * Save clipboard contents
     * Send Ctrl+C to focused window
     * Read clipboard text
     * Restore clipboard

2. **Speech Synthesis (Local SAPI Voices)**

   * Use WinRT API: `Windows.Media.SpeechSynthesis.SpeechSynthesizer`
   * Enumerate voices: `SpeechSynthesizer.AllVoices`
   * ~~Prefer voices whose display name contains "(Natural)"~~ — removed; "(Natural)" voices never appear in `AllVoices` (see correction note above). Just list the available voices alphabetically.

3. **Audio Playback + Speed**

   * Use WinRT `Windows.Media.Playback.MediaPlayer`
   * Feed `SpeechSynthesisStream` into MediaPlayer
   * Set speed via `MediaPlayer.PlaybackSession.PlaybackRate`
   * Provide UI to set speed 0.1–4.0 (step 0.1)

> ⚠️ Uncertainty note: PlaybackRate support and max rate can vary by Windows version/stack. Implement the UI range 0.1–4.0, but clamp to what `PlaybackSession` accepts at runtime if needed (detect exceptions / invalid values and fallback). Confidence ~0.7 that 0.5–2.0 always works; 4.0 may or may not depending on system.

---

## Dependencies (NuGet)

Codex: install these packages:

* `Microsoft.Windows.SDK.Contracts` (for WinRT types like `Windows.Media.SpeechSynthesis`)
* `Microsoft.CsWinRT` (if needed for WinRT interop in .NET; only add if compilation requires it)
* Optional: `H.NotifyIcon.Wpf` (clean tray icon integration)

Also ensure:

* Target framework: `net10.0-windows`
* Enable Windows APIs:

  * `UseWPF = true`

---

## UI Requirements (WPF)

Create a small window (can be hidden by default) with:

* **Voice dropdown** (default to the first available voice — "(Natural)" preference removed, see correction note above)
* **Speed control**

  * Slider min 0.1 max 4.0
  * Tick frequency 0.1
  * Label showing current speed `1.0x`
  * Buttons: `-0.1` and `+0.1`
* Buttons:

  * `Read Selection`
  * `Pause`
  * `Resume`
  * `Stop`
* Tray icon menu:

  * `Read Selection` (same as hotkey)
  * `Show/Hide`
  * `Exit`

---

## Global Hotkey

Register system-wide hotkey, default:

* `Win + Alt + R`

Implementation:

* Use `RegisterHotKey` via P/Invoke in a hidden window (or WPF window handle)
* On hotkey:

  1. Acquire selection text
  2. If empty: show toast/balloon “No selected text found”
  3. Speak it

---

## Selection Acquisition Details

### A) UI Automation selection (first attempt)

Algorithm:

1. Get foreground window handle
2. Get focused element via UIA
3. Check patterns supported:

   * `TextPattern` / `TextPattern2`
4. If available:

   * `GetSelection()`
   * Concatenate ranges → string
5. Trim; if not empty return

### B) Clipboard Ctrl+C fallback (second attempt)

Algorithm:

1. Save clipboard:

   * Text (and ideally formats). For personal use, saving/restoring text is OK; but try to preserve *all formats* if practical.
2. Send `Ctrl+C` to foreground app:

   * `SendInput` with key down/up for Ctrl+C
3. Wait a short time (e.g. 50–150ms with retry loop up to 500ms)
4. Read `Clipboard.GetText()`
5. Restore clipboard
6. Return text if non-empty

Notes:

* Use STA thread for clipboard operations (WPF UI thread is STA).
* Avoid leaving clipboard altered.

---

## Speech + Playback Details

### Voice enumeration and selection

* On startup: load `SpeechSynthesizer.AllVoices`
* Populate dropdown with `DisplayName`
* Choose default: the first available voice (the former "first `(Natural)` voice" rule was removed — see correction note above)

### Speak flow

Implement a `TtsService` that exposes:

* `Task SpeakAsync(string text, Voice voice, double rate)`
* `Pause()`, `Resume()`, `Stop()`
* `IsPlaying`, `IsPaused`

Implementation outline:

1. `SpeechSynthesizer synth = new SpeechSynthesizer()`
2. `synth.Voice = selectedVoice`
3. `SpeechSynthesisStream stream = await synth.SynthesizeTextToStreamAsync(text)`
4. Create `MediaSource` from stream
5. Set `mediaPlayer.Source`
6. Set `mediaPlayer.PlaybackSession.PlaybackRate = rate` (try/catch, clamp on failure)
7. `mediaPlayer.Play()`

Stop/pause:

* `mediaPlayer.Pause()`
* `mediaPlayer.Play()`
* `mediaPlayer.Source = null` to stop

---

## Implementation Checklist (Codex must follow)

### Files / Classes

Create these (names matter):

* `App.xaml` / `App.xaml.cs`
* `MainWindow.xaml` / `MainWindow.xaml.cs` (UI + bindings)
* `Tray/TrayIconManager.cs` (tray menu wiring)
* `Hotkeys/GlobalHotkey.cs` (RegisterHotKey wrapper)
* `Selection/SelectionReader.cs` (UIA + clipboard fallback)
* `Tts/TtsService.cs` (SpeechSynthesizer + MediaPlayer)
* `Models/VoiceOption.cs` (DisplayName, Id, WinRT voice reference)
* `ViewModels/MainViewModel.cs` (MVVM-ish; keep it simple)

### Functional Requirements

* [ ] App runs in tray; closing window does not exit (minimize-to-tray behavior)
* [ ] Hotkey triggers “Read Selection”
* [ ] Tray menu has “Read Selection”
* [ ] Voice dropdown lists installed voices (no "(Natural)" preference — see correction note above)
* [ ] Speed adjustable:

  * slider range 0.1–4.0
  * +/- 0.1 buttons
  * value shown as `X.Xx`
* [ ] Read selection:

  * tries UIA
  * falls back to clipboard Ctrl+C
  * if still empty: notify user
* [ ] Playback: Play/Pause/Resume/Stop works
* [ ] Persist settings (voice + speed + hotkey) to a simple local JSON file in `%AppData%/ReadSelectedTextTts/settings.json`

### Non-Goals (explicitly do NOT do)

* No cloud TTS (no Azure calls)
* No telemetry
* No complex installer required (optional later)

---

## “Right-click” Option (Best-Effort Plan)

Implement **tray icon right-click menu** (guaranteed).

Optional stretch goal:

* Add Windows Explorer context menu “Read Aloud” for `.txt` files only (simple registry command that launches app with file path).
* Do **not** attempt a system-wide “selected text” context menu injection across all apps.

---

## Testing Steps (manual)

Codex: provide a short README with these tests:

1. Select text in Notepad → press Win+Alt+R → it reads
2. Select text in Chrome address bar / webpage → hotkey → reads
3. Select text in Word → hotkey → reads
4. Speed changes affect playback audibly
5. App works offline (disable network)
6. Clipboard restored after reads (copy something, read selection, paste—should match original)

---

## Deliverables

* The complete WPF app source code
* A `README.md` with build/run instructions
* `settings.json` persistence
* No external services required

---

## Notes for Codex (edge cases to handle)

* If `SpeechSynthesizer.AllVoices` returns none, show a clear error (“No Windows voices installed.”)
* ~~If `(Natural)` voices aren't installed, still allow using available voices~~ — moot; "(Natural)" voices are never available via this API (see correction note above)
* If `PlaybackRate` throws or clamps silently, keep UI consistent but log actual applied rate in a debug log file
