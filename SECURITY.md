# Security Policy

## Reporting A Vulnerability

Please do not open public GitHub issues for suspected security problems.

If you discover a vulnerability, report it privately to the repository maintainer first and include:

- a short description of the issue;
- affected files or flows;
- reproduction steps, if available;
- impact and any suggested mitigation.

## Sensitive Data Handling

This project can process:

- Discord bot credentials;
- OpenRouter API credentials;
- credentials for a local or hosted transcription provider;
- recorded meeting audio;
- generated transcripts and summaries.

Operators should keep `.env`, runtime `data/`, and any locally supplied native libraries out of version control and out of public release artifacts.
