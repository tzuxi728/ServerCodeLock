# Changelog

All notable changes to ServerCodeLock are documented here.

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versioning follows the plugin `[Info]` attribute.

## [1.3.0] — 2026-09

### Added

- Wipe-scoped state file (`ServerCodeLock/state.json`) with `WipeId`, `Authorized`, and `Attempts`
- Automatic allow-list reset when a new save is detected
- Idle gate timeout with configurable kick reason (`0` disables)
- Weak-PIN warning on load for trivial codes (`1234`, repeats, short sequences)
- Buffered file logging and `DebugVerbose`
- `scl.status` console summary
- English / Russian CUI strings
- Legacy authorized-file migration into `state.json`

### Changed

- Carbon builds exclude fragile game-method hooks at compile time to avoid `Invalid IL code` / `Signature not found`
- Gate freeze now combines input lock, position snap-back, metabolism freeze, and anti-cheat pause
- Keypad input is rate-limited

### Security

- Attempt counters persist across reconnects for the current wipe
- Kick threshold and ban threshold are independently configurable
