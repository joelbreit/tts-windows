# Windows "Natural" / "Natural HD" Voices Are Unavailable to This App

> **TL;DR — Do not spend time trying to make this app use the Windows 11
> "Natural" voices (Ava, Aria, Andrew, etc.).** They are deliberately walled off
> from every public Windows TTS API. This is **not** a bug, a missing install
> step, a registration race, or something a reboot fixes. It has been
> investigated thoroughly and repeatedly. If you want better voices, use a cloud
> TTS API or a bundled local model — see [`tts-options.html`](./tts-options.html).

This document exists because the assumption "we can just use the installed
Windows Natural voices" keeps resurfacing. It was the founding premise of the
app (see `Codex-Instructions.md`), and it is wrong. Read this before touching
voice enumeration.

---

## The one-paragraph explanation

The app enumerates voices through
`Windows.Media.SpeechSynthesis.SpeechSynthesizer.AllVoices`. That API reads
voice tokens from one specific registry hive. The Windows 11 "Natural" voices do
**not** register there — they don't register in any of the public speech hives.
They ship as self-contained Appx packages carrying a private neural engine, and
they are discoverable only by **Narrator** and the accessibility stack via a
private app-extension contract. No supported public API can enumerate or invoke
them. Therefore `AllVoices` returns only the legacy SAPI voices (David, Mark,
Zira) and **always reports 0 Natural voices**, no matter how many Natural voices
you install.

---

## Evidence (verified 2026-06-08 on the developer's machine)

The Microsoft Ava "Natural HD" voice was installed via Windows Settings, and the
machine was rebooted. The app still reported `Loaded 3 voice(s). Natural voices:
0.` Investigation found:

### 1. The voice is installed correctly — as an Appx package, not a SAPI voice

```
Name            : MicrosoftWindows.Voice.en-US.AvaHD.1
Status          : Ok
InstallLocation : C:\Program Files\WindowsApps\MicrosoftWindows.Voice.en-US.AvaHD.1_1.0.3.0_x64__cw5n1h2txyewy
```

The package contains its **own private neural TTS engine** (~580 MB of model
files), not a voice that plugs into the OS speech stack:

```
hd_am_v5_decoder.bin            ~98 MB    (acoustic model decoder)
hd_am_v5_encoder.bin            ~38 MB    (acoustic model encoder)
hd_device_vocoder_v6_streaming.bin ~127 MB (neural vocoder)
am_v5_encoder.bin / decoder.bin           (standard-quality variant)
MSTTSLocEnUS.dat                ~36 MB    (locale data)
Tokens.xml, AppxManifest.xml
```

### 2. It targets a registry hive that does not exist on the machine

`Tokens.xml` declares the voice under:

```
HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Speech Server\v11.0\Voices\Tokens
   └─ TTS_MS_en-US_AvaNeural_11.0  →  "Microsoft Ava (Natural HD) - English (United States)"
      CLSID = {a12bdfa1-c3a1-48ea-8e3f-27945e16cf7e}
```

But on the machine, `HKLM\SOFTWARE\Microsoft\Speech Server` **does not exist at
all** (`Test-Path` → `False`), and the engine CLSID
`{a12bdfa1-…}` is **not** registered in `HKEY_CLASSES_ROOT`. So even the
COM path the manifest implies is not wired up for third-party use.

### 3. It is invisible to all three public Windows TTS surfaces

| API | Reads from hive | Sees Ava? |
|-----|-----------------|-----------|
| `Windows.Media.SpeechSynthesis` (OneCore) — **what this app uses** | `…\Speech_OneCore\Voices\Tokens` | ❌ No |
| `System.Speech` / SAPI 5 | `…\Speech\Voices\Tokens` | ❌ No |
| (the voice's own declared hive) | `…\Speech Server\v11.0\…` | ❌ hive absent |

Live probes returned only:

- OneCore `AllVoices`: David, Mark, Zira
- SAPI `GetInstalledVoices()`: David Desktop, Zira Desktop
- No token mentioning `Ava` / `Neural` / `Natural` in **any** public voice store
  (HKLM or HKCU).

### 4. It is discovered only through a Narrator-private contract

`AppxManifest.xml` registers the package via an app extension:

```xml
<uap3:AppExtension Name="com.microsoft.voice.model.1" Id="VoiceAvaEnUS" ...>
  <uap3:Properties><LocaleId>en-US</LocaleId></uap3:Properties>
</uap3:AppExtension>
```

`com.microsoft.voice.model.1` is the contract Narrator (and the Windows
accessibility stack) uses to enumerate these voices. It is not a public TTS API.
Microsoft intentionally reserves these voices for Narrator.

---

## Why the usual "fixes" don't work

- **Installing more Natural voices** — They all register the same way; the app
  still sees zero. More installs = more invisible packages.
- **Rebooting / re-running** — Already tried. The hive and CLSID are absent by
  design, not pending.
- **Switching to `System.Speech` (SAPI)** — SAPI reads a *different* public hive
  that the Natural voices also avoid. It sees even fewer voices.
- **Looking for a `(Natural)` substring in `DisplayName`** — Was the app's
  original heuristic. It can never match, because the voices never reach the
  enumeration in the first place.

## The only ways to actually reach these voices (and why we don't)

1. **Reverse-engineer the COM engine** — manually register CLSID
   `{a12bdfa1-…}`, load the `.bin` models, and drive the private engine.
   Fragile, unsupported, breaks on every Windows/voice update, and of dubious
   licensing. Not worth it for a personal-use app.
2. **Azure Speech "Embedded" / container TTS** — runs equivalent neural voices
   locally, but requires a special commercial license and separately-licensed
   model files. Not freely available.

## What to do instead

Use a real TTS provider. The same Ava voice is available as the cloud voice
**`en-US-AvaNeural`** through Azure AI Speech, within its free tier (≈500k
chars/month — more than enough for personal use). Full comparison of cloud APIs
and bundled local models (Piper, Kokoro) is in
[`tts-options.html`](./tts-options.html).

---

## For future LLMs reading the codebase

If you find yourself thinking *"the app should just prefer the installed
`(Natural)` voices"* — **stop.** That impulse is exactly what this document
exists to correct. The Natural-voice preference logic was removed from the code
on purpose. `SpeechSynthesizer.AllVoices` will only ever return the legacy SAPI
voices on Windows, and adding back a `(Natural)` filter will silently match
nothing. Point the user to a cloud/local TTS provider instead.
