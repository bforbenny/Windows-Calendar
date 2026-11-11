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

- R1. Runtime: Windows AI `LanguageModel` (`Microsoft.Windows.AI.Text`), using `GenerateStructuredJsonResponseAsync` (JSON-Schema-constrained output) with Temperature 0 and TopK 1. Schemas use only `type`, `properties`, `required`, `enum`, `items`; empty values are "" or 0. No model file, server or driver is shipped.
- R1a. Packaging in the WPF app ([app-platform](app-platform.md) R2, R3):
  - reference only the `Microsoft.WindowsAppSDK.AI` component package (2.6.5, the latest stable on 2026-11-11; 2.6.4 or later needs no LAF unlock, per the Windows App SDK 2.6.4 release notes; approved by the user on 2026-11-11), which depends on `.Base` and `.Foundation` only; never the `Microsoft.WindowsAppSDK` metapackage or `.WinUI`, so no WinUI code returns (WinUI was dropped for its dimmed-scrolling bug);
  - the app stays packaged (MSIX identity is required); the manifest keeps the `systemAIModels` capability and `MaxVersionTested` 10.0.26226.0 or later, and the project sets `AppxOSMinVersionReplaceManifestVersion` and `AppxOSMaxVersionTestedReplaceManifestVersion` to false so the build does not overwrite them;
  - the package then depends on the Windows App Runtime framework package again; installing it on a new PC follows [app-updates](app-updates.md);
  - the AI code lives only in the app project, behind the R10 interface; `WindowsCalendar.Core` gains no Windows App SDK reference.
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
- R12. Spike gate: before any feature code, a Release (ReadyToRun) MSIX build of the WPF app on the Ryzen AI 7 350 first confirms that the R1a package set builds, installs and reports a ready state (if the component package alone does not work, stop and revisit R1a before trying the metapackage), then runs ~30 sample phrases through the service. Record ready state, cold and warm latency (p50/p95), and invalid-JSON rate, for structured output and plain generation. Go only if warm p95 ≤ 1.5 s and invalid JSON ≤ 5%; otherwise stop and revisit this PRD.
  - The spike is short and does not block other work: [quick-add](quick-add.md) and [reminders-notifications](reminders-notifications.md) go ahead in parallel. Only the NL features wait for the result.
  - Measure each request mode with two schemas: the full [nl-quick-add](nl-quick-add.md) R6 schema and a reduced one holding only the fields the rules left empty (most phrases need only the date fields). Bound every request with a 10 s timeout and count timeouts as failures, so the known hang (WindowsAppSDK issue #6871) shows in the numbers.
  - Known evidence before the spike (2026-11-11): issue #6871, on a Ryzen AI 7 PRO 350 with SDK 2.5.4-experimental, reports structured JSON sometimes not completing within 25 s, and 3–8 s when it does complete (plain generation 7.9 s). Phi Silica is to be replaced by Aion Instruct on retail devices in January 2027; a no-go result is re-checked once Aion Instruct is on this PC.

## Spike results

- 2026-11-11, Ryzen AI 7 350, Windows 10.0.26200, `Microsoft.WindowsAppSDK.AI` 2.6.5:
  - The component package builds in Windows App SDK self-contained mode: no Windows App Runtime package dependency, runtime DLLs copied into the app, and the non-XAML `Microsoft.UI.*` files of `Microsoft.WindowsAppSDK.InteractiveExperiences` added to the package. No WinUI or `Microsoft.UI.Xaml` assemblies.
  - The two `Appx...ReplaceManifestVersion` properties have no effect with the MSIX build tools 1.7: the Windows.Desktop `MaxVersionTested` is rewritten to 10.0.26100.0. Setting it to 10.0.26226.0 by hand did not change the result below.
  - `LanguageModel.GetReadyState()` returned `DisabledByUser`, although the systemAIModels consent is Allow both overall and for the app and Settings > System > AI components lists AI Phi Silica 1.2610.1069.0. Two causes were found:
    - the MSIX build tools add a `Windows.Desktop` device family from `TargetPlatformVersion` (10.0.26100.0) unless `AppxAddDefaultTargetDeviceFamilyItem` is false; with it false, the built manifest keeps 10.0.26226.0;
    - the `WSAIFabricSvc` Windows service was set to Disabled on this PC; `EnsureReadyAsync` then failed with 0x80070422 (service disabled). The user set it to Automatic (delayed start) and started it.
  - After both fixes (package 1.0.8.0): ready state `Ready`, `CreateAsync` 156 ms cold. 30 phrases per mode, 10 s timeout, Temperature 0, TopK 1:
    - structured, full schema: 30 of 30 timed out;
    - structured, reduced schema: 30 of 30 timed out;
    - plain generation + System.Text.Json: 25 timed out, 3 invalid JSON, 2 ok (8.2 s and 8.3 s).
  - A cancelled request may keep the NPU busy and slow the next one, but the completed plain requests alone are over 5 times the 1.5 s limit, matching issue #6871 (plain 7.9 s).
  - Decision: **NO-GO**. Quick add and search stay on rules and FTS; NL feature work stays parked. Re-check when Aion Instruct is on this PC (January 2027).

## Out of scope

- Other runtimes (Foundry Local, Lemonade, Windows ML with bundled ONNX models), cloud models, embeddings and semantic search.
- Languages other than English.

## Acceptance criteria

- [x] Spike results (R12) recorded in this file, with the go/no-go decision.
- [ ] The built package contains no WinUI assemblies (`Microsoft.WinUI.dll`, `Microsoft.UI.Xaml.*`).
- [ ] With the setting off, no model API is called and no model download starts.
- [ ] With the setting on and the model unavailable, Settings shows why, and quick add and search behave as without AI.
- [ ] A model string containing a zero-width character is cleaned before use (unit test).
- [ ] A model title containing a word not in the input is discarded (unit test).
- [ ] With Battery Saver on, no model request is made.
- [ ] The Debug eval command produces a report in LocalState.

## Dependencies

- [app-platform](app-platform.md), [app-updates](app-updates.md), [settings-page](settings-page.md), [timezone-handling](timezone-handling.md)

## Open questions

- Windows build required for Phi Silica on retail Windows 11: confirm in the spike.
- Whether the single-project MSIX build tools add the Windows App Runtime package dependency automatically when only `Microsoft.WindowsAppSDK.AI` is referenced: confirm in the spike.
- Microsoft's Aion Instruct model replaces Phi Silica on retail devices in January 2027 (Microsoft's Phi Silica page, checked 2026-11-11), and needs no LAF token. Not yet known: whether it keeps the same `LanguageModel` API. Re-run the eval sets when it arrives.
