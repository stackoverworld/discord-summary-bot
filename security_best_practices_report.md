# Security Best Practices Report

## Executive Summary

The repository has been cleaned up for public GitHub hosting. Live credentials and recorded runtime artifacts were removed from the working tree, setup now expects local environment variables, and documentation was updated to keep private data and local native dependencies out of version control.

## Critical Findings

### SBP-001: Live credentials were present in a local `.env` file

Impact: publishing the previous working tree would have exposed Discord and OpenAI credentials.

Status: fixed.

Relevant updates:

- [.gitignore](/Users/moiseencov/Downloads/Projects/discord-summary-bot/.gitignore#L1) keeps local env files, runtime data, build output, and local native binaries out of version control.
- [src/DotEnv.cs](/Users/moiseencov/Downloads/Projects/discord-summary-bot/src/DotEnv.cs#L5) now preserves already-set environment variables so deployment-time secrets can override local `.env` values safely.
- [README.md](/Users/moiseencov/Downloads/Projects/discord-summary-bot/README.md#L85) documents `.env.example` as the committed template and `.env` as local-only.

### SBP-002: Real captured meeting data was present in `data/`

Impact: publishing the previous working tree would have exposed audio-derived transcripts, summaries, and Discord metadata.

Status: fixed.

Relevant updates:

- Runtime `data/` contents were removed from the working tree and replaced with [data/.gitkeep](/Users/moiseencov/Downloads/Projects/discord-summary-bot/data/.gitkeep#L1).
- [README.md](/Users/moiseencov/Downloads/Projects/discord-summary-bot/README.md#L137) now explicitly marks `data/` as runtime-only and private.
- [README.md](/Users/moiseencov/Downloads/Projects/discord-summary-bot/README.md#L151) adds a privacy and consent section for operators.

## Medium Findings

### SBP-003: Session metadata previously leaked absolute local filesystem paths

Status: fixed.

Relevant updates:

- [src/FileSessionStore.cs](/Users/moiseencov/Downloads/Projects/discord-summary-bot/src/FileSessionStore.cs#L31) provides a session-relative audio path helper.
- [src/VoiceSession.cs](/Users/moiseencov/Downloads/Projects/discord-summary-bot/src/VoiceSession.cs#L324) persists relative audio paths instead of absolute local paths.

### SBP-004: Public repository setup around native binaries was unclear

Status: fixed.

Relevant updates:

- [DiscordSummaryBot.csproj](/Users/moiseencov/Downloads/Projects/discord-summary-bot/DiscordSummaryBot.csproj#L11) supports `LIBDAVE_PATH` or a local ignored `native/libdave.dylib`.
- [native/README.md](/Users/moiseencov/Downloads/Projects/discord-summary-bot/native/README.md#L1) documents how contributors should provide `libdave` locally.

## Low Findings

### SBP-005: Missing public-facing security guidance

Status: fixed.

Relevant updates:

- [SECURITY.md](/Users/moiseencov/Downloads/Projects/discord-summary-bot/SECURITY.md#L1) adds a basic vulnerability reporting policy and handling guidance for sensitive local data.

## Remaining Manual Action

### SBP-006: No open-source license is selected yet

Status: not fixed automatically.

Reason:

- Choosing a license has legal and project-governance consequences, so it should be selected intentionally by the maintainer.

Suggested next step:

- Add a `LICENSE` file before publishing the repository publicly.
