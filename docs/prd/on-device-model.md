---
description: Shared on-device language model service (Windows AI LanguageModel on the NPU) used by natural-language quick add and search
when-to-use: Before working on the Windows AI LanguageModel integration, the AI setting, the model request queue, the phrase eval harness, or the NPU spike
tags: [ai, npu, platform, privacy]
status: pending
---

# On-device model

## Summary

One app-wide service wraps the Windows AI `LanguageModel` (Phi Silica on Copilot+ NPUs) for [nl-quick-add](nl-quick-add.md) and [nl-search](nl-search.md). It handles availability, the user's on/off setting, request ordering, output cleaning and privacy, and it carries the phrase eval harness. A spike on the target laptop gates all feature work. Without the model, both features fall back to their rule or FTS behaviour.

## Requirements

- R1. Runtime: Windows AI `LanguageModel` from the Windows App SDK, using `GenerateStructuredJsonResponseAsync` (JSON-Schema-constrained output) with Temperature 0 and TopK 1. Schemas use only `type`, `properties`, `required`, `enum`, `items`; empty values are "" or 0. No model file, server or driver is shipped; the manifest's existing `systemAIModels` capability is used.
- R2. Setting: "Use on-device AI" in Settings ([settings-page](settings-page.md)), off by default. When off, the service is never called and `EnsureReadyAsync` never runs. Turning it on checks `GetReadyState()`; if `NotReady`, it calls `EnsureReadyAsync()` with progress shown in Settings. If unsupported, disabled by the user or failing, the setting shows why and the features stay on rules/FTS. Any exception from the API (including on Windows builds without it) counts as unavailable.
- R3. Lifetime: one model instance, created on first use after the setting is on, reused for the session, disposed on exit.
- R4. Queue: one request runs at a time. Quick-add requests take priority over search requests. A newer request from the same feature cancels its older pending or running request.
- R5. Power: when Windows Battery Saver is on, the service reports unavailable (no indicator, rules/FTS only).
- R6. Output cleaning: every string from the model has zero-width, bidi and other Unicode format (Cf) characters removed and whitespace collapsed before any use. Output that is not schema-valid JSON is discarded. Content-moderation blocks count as a failed request.
- R7. Grounding: a text value from the model (title, location, attendee, keyword) is used only if, after case-folding, each of its words appears in the user's input. Otherwise that field is discarded.
- R8. Prompt context: today's date, weekday and Windows time zone, plus the names a feature needs (calendars, task lists, accounts). The model never returns absolute dates; features send relative forms that code resolves and validates.
- R9. Privacy: phrases, prompts and model output are never logged. Errors are logged by type only. The only exception is the eval report (R11), which contains test phrases only.
- R10. Testability: features call the service through an interface, so merge, validation and UI logic are unit-tested with a fake.
- R11. Eval harness: phrase sets with a fixed reference time and expected fields live in the repo (one per feature). A Debug-only command runs a set against the real model in the packaged app and writes a pass/fail report with latency to LocalState.
- R12. Spike gate: before any feature code, a Release (trimmed, ReadyToRun) build on the Ryzen AI 7 350 runs ~30 sample phrases through the service. Record ready state, cold and warm latency (p50/p95), and invalid-JSON rate, for structured output and plain generation. Go only if warm p95 ≤ 1.5 s and invalid JSON ≤ 5%; otherwise stop and revisit this PRD.

## Out of scope

- Other runtimes (Foundry Local, Lemonade, Windows ML with bundled ONNX models), cloud models, embeddings and semantic search.
- Languages other than English.

## Acceptance criteria

- [ ] Spike results (R12) recorded in this file, with the go/no-go decision.
- [ ] With the setting off, no model API is called and no model download starts.
- [ ] With the setting on and the model unavailable, Settings shows why, and quick add and search behave as without AI.
- [ ] A model string containing a zero-width character is cleaned before use (unit test).
- [ ] A model title containing a word not in the input is discarded (unit test).
- [ ] With Battery Saver on, no model request is made.
- [ ] The Debug eval command produces a report in LocalState.

## Dependencies

- [app-platform](app-platform.md), [settings-page](settings-page.md), [timezone-handling](timezone-handling.md)

## Open questions

- Windows build required for Phi Silica on retail Windows 11: confirm in the spike.
- Microsoft's Aion Instruct model may replace Phi Silica (timeline unconfirmed); re-run the eval sets when it does.
