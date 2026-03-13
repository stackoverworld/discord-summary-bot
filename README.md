# Discord Summary Bot (.NET + NetCord)

Self-hosted Discord bot that automatically joins configured voice channels, captures speech per participant, transcribes utterances through an OpenAI-compatible speech-to-text endpoint, builds rolling checkpoints during the call, and posts a final meeting summary with action items back into a Discord text channel via OpenRouter.

This repository is prepared for public GitHub hosting:

- secrets are expected in local `.env` only;
- recorded call data stays under local `data/` and is gitignored;
- Railway deployment is expected to run through the root `Dockerfile`.

## Why The Previous Version Did Not Work

Short version:

- the `discord.js + @discordjs/voice` bot appeared to join the call, but the voice receive transport never became fully operational;
- Discord showed the bot in voice, but audio packets were not reaching the app reliably;
- the issue was not “you did not talk enough”, it was that the Node voice layer in this environment did not establish stable audio capture.

That is why this repository was moved to `.NET + NetCord`, where the voice/DAVE stack is currently much more viable.

## What The Current Version Does

- watches one or more designated voice channels;
- joins automatically when the first real participant enters;
- ends the session automatically when the last real participant leaves;
- records speech per user instead of using diarization on a mixed track;
- stores `.wav` chunks, transcript, checkpoints, and final summary locally in `data/`;
- posts the final summary and attaches `summary.md` and `transcript.md` into the configured text channel.

## Requirements On macOS

### 1. .NET SDK

According to the official NetCord documentation, it requires `.NET 10 or higher`.

Install `.NET 10 SDK`.

Check:

```bash
dotnet --version
```

### 2. Native Voice Dependencies

According to the official NetCord documentation, voice support depends on native libraries:

- `libdave`
- `libsodium`
- `opus`

The most practical path on macOS:

```bash
brew install libsodium opus
```

Install `libdave` using whatever method is available in your environment. For Railway, the included `Dockerfile` builds `libdave` inside the container, so you do not need to commit native binaries into the repo.

## Discord Setup

In the Discord Developer Portal, the bot should have:

- `View Channels`
- `Connect`
- `Speak`
- `Use Voice Activity`
- `Send Messages`
- `Attach Files`
- `Read Message History`

Required intents:

- `Guilds`
- `Guild Voice States`
- `Guild Members / Guild Users` access for participant resolution

## Environment

Copy `.env.example` to `.env` and fill it in:

```bash
DISCORD_BOT_TOKEN=
DISCORD_GUILD_ID=
MONITORED_VOICE_CHANNEL_IDS=123456789012345678,234567890123456789
SUMMARY_TEXT_CHANNEL_ID=345678901234567890
OPENROUTER_API_KEY=
OPENROUTER_MODEL=openrouter/free
OPENROUTER_TEMPERATURE=0.2
OPENROUTER_HTTP_REFERER=
OPENROUTER_APP_NAME=Discord Summary Bot

TRANSCRIPTION_API_BASE_URL=http://localhost:8000/v1/
TRANSCRIPTION_API_KEY=local-dev
TRANSCRIPTION_MODEL=Systran/faster-distil-whisper-large-v3
TRANSCRIPTION_LANGUAGE=

VOICE_READY_TIMEOUT_MS=15000
SESSION_END_GRACE_MS=30000
VOICE_RECONCILE_INTERVAL_MS=10000
UTTERANCE_SILENCE_MS=1500
MIN_UTTERANCE_MS=1200
STARTUP_RETRY_COOLDOWN_MS=15000
CHECKPOINT_INTERVAL_UTTERANCES=24
MAX_TRANSCRIPTION_CONCURRENCY=2
DATA_DIR=./data
LOG_LEVEL=Information
```

Notes:

- `.env` is intentionally local-only and ignored by git.
- `.env.example` is the only env file meant to be committed.
- Real deployment environment variables take precedence over `.env`.
- Summaries go through OpenRouter. `openrouter/free` is the default router model in this repo.
- Transcription defaults to a local OpenAI-compatible server at `http://localhost:8000/v1/`.
- `data/` contains transcripts, summaries, and audio derived from real calls, so keep it private unless participants explicitly agreed to share it.

### Free Local Speech-To-Text

This repo is configured by default for a free self-hosted transcription server instead of paid hosted speech-to-text.

One practical option is `faster-whisper-server`, which exposes an OpenAI-compatible `audio/transcriptions` API. If you run it locally on port `8000`, the default `.env.example` values already match the expected endpoint shape.

## Local Run

From the project root:

```bash
dotnet restore
dotnet run
```

## Publish

```bash
dotnet publish -c Release
```

Before pushing to GitHub, make sure the repo does not contain your local `.env`, recorded `data/`, `.dotnet-home/`, `bin/`, or `obj/`.

## Railway Deploy

Use Docker deployment for Railway:

1. Keep the root `Dockerfile` committed in this repository.
2. Push the repo to GitHub.
3. In Railway, open the service.
4. Go to `Settings`.
5. Set `Builder` to `Dockerfile`.
6. Redeploy.

The `Dockerfile` installs `libicu72`, `libsodium23`, and `libopus0`, then builds `libdave` inside the same Linux image so Discord voice works without committed native binaries.

## Output Layout

Each session is stored like this:

```text
data/<session-id>/
  audio/
  session.json
  transcript.md
  summary.md
```

`session.json` stores audio paths relative to the session folder, so it does not leak an absolute local filesystem path.

## Privacy And Consent

This bot records voice activity, stores audio snippets, writes transcripts, and generates meeting summaries. If you operate it, you are responsible for:

- obtaining any required participant consent;
- securing and cleaning up local recordings, transcripts, and summaries;
- defining a retention policy appropriate for your team, community, or organization.

## Important Notes

- Discord voice receive is still the riskiest part of the whole system.
- Even with the .NET stack, you should test it in real voice calls on your own server before treating it as production-ready.
- The current architecture is much closer to a workable production setup, but this still needs real-world validation in your environment.
- Choose and add an explicit open-source `LICENSE` before publishing the repository publicly.

## Useful References

- [NetCord installation](https://netcord.dev/guides/getting-started/installation.html)
- [NetCord voice guide](https://netcord.dev/guides/basic-concepts/voice.html)
- [NetCord native dependencies](https://netcord.dev/guides/basic-concepts/installing-native-dependencies.html)
- [OpenRouter quickstart](https://openrouter.ai/docs/quickstart)
- [OpenRouter free model router](https://openrouter.ai/docs/api-reference/overview#free-models)
- [faster-whisper-server](https://github.com/fedirz/faster-whisper-server)
