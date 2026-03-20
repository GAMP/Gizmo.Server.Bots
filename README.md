# Gizmo.Server.Bots

Custom bot modules for Gizmo Server. Each bot is a self-contained Gizmo module that is loaded directly by the server at runtime.

## Purpose

This repository hosts our own bot implementations. Functionality varies per bot and may include:

- **Messenger bots** — user verification, registration, and confirmation code delivery via messaging platforms (Telegram, Viber, etc.)
- **Report bots** — automated report generation and delivery
- **Event bots** — notifications and alerts triggered by server events

## Projects

| Project | Description |
|---|---|
| `Gizmo.Server.Bots.Telegram` | Telegram bot for user verification, registration, and confirmation code delivery |

## Dependencies

Projects use conditional references — submodule project references when available locally, NuGet package fallback otherwise. This supports both development (with submodules) and CI/standalone builds.
