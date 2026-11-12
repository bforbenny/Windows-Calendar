---
description: Show-as (Busy / Free / Tentative / Out of office / Working elsewhere) on events, mapped to Graph and CalDAV
when-to-use: Before working on the show-as field, its sync mapping, or how free/tentative/OOF events are drawn
tags: [events, show-as, free-busy, ui]
status: closed
---

# Event show-as

## Summary

Each event has a show-as value that controls how it blocks your availability for others. It is editable and round-trips without loss. The available values depend on the backend: Graph supports all five, CalDAV supports Busy and Free.

## Requirements

- R1. Mapping:

  | App value | Graph `showAs` | CalDAV |
  | --- | --- | --- |
  | Busy | `busy` | `TRANSP:OPAQUE` |
  | Free | `free` | `TRANSP:TRANSPARENT` |
  | Tentative | `tentative` | read-only (see R3) |
  | Out of office | `oof` | read-only (see R3) |
  | Working elsewhere | `workingElsewhere` | not available |

- R2. The editor offers only the values the item's backend supports: Graph shows all five, CalDAV shows Busy / Free.
- R3. CalDAV events carrying `X-MICROSOFT-CDO-BUSYSTATUS` show that value (e.g. Tentative, OOF). If the user changes show-as, the app writes `TRANSP` and updates `X-MICROSOFT-CDO-BUSYSTATUS` to match; otherwise both are round-tripped unchanged.
- R4. Defaults for new events: timed events → Busy, all-day events → Free. Toggling all-day re-applies the default only while the user has not changed show-as.
- R5. Visual style in all views, using the calendar colour steps from [ui-colour-theme](ui-colour-theme.md) R6. Hatching never sits behind text, so text contrast stays at WCAG AA:

  | Show-as | Fill | Left bar | Border | Icon |
  | --- | --- | --- | --- | --- |
  | Busy | calendar fill | solid | none | none |
  | Free | none (surface) | solid | 1 epx, swatch colour | none |
  | Tentative | none (surface) | hatched | 1 epx dashed, swatch colour | none |
  | Out of office | calendar fill | solid | none | "away" glyph before the title |
  | Working elsewhere | none (surface) | solid | 1 epx dotted, swatch colour | "building" glyph before the title |

  Icons come from Segoe Fluent Icons. Styles work in light, dark and high-contrast themes.
- R6. Show-as is editable offline and synced via the outbox; for recurring events the scope prompt applies.
- R7. Graph `unknown` is displayed as Busy and preserved unless the user edits it.

## Interim (first slice)

- Built: R1 to R7. The editor's "Show as" drop-down sits under Location. Unanswered MS365 invitations are drawn by their show-as (usually Tentative). This replaces the muted outline from [attendees-invitations](attendees-invitations.md) interim, because that outline looked the same as Free.
- Glyphs: Out of office uses Airplane (U+E709) and Working elsewhere uses CityNext (U+EC06) from Segoe Fluent Icons.
- A CalDAV event carrying Working elsewhere in `X-MICROSOFT-CDO-BUSYSTATUS` shows it read-only, like Tentative and Out of office.
- `X-MICROSOFT-CDO-BUSYSTATUS` is written when the event already has it, and when the value is neither Busy nor Free (for example an MS365 event moved to Radicale).
- A CalDAV series is drawn with its master's show-as. A changed occurrence keeps its own value in the editor and on the server, but its card shows the series' style. Titles of changed occurrences behave the same way.
- Existing CalDAV events get their show-as from the stored data once, on the first start after the update (store v11).

## Pending user check

Built on branch `event-show-as` (2026-11-06). The branch is stacked on `duplicate-event`. Nothing here is verified in the app or merged yet:

- [x] Show as Out of office on an MS365 event, then check that Outlook shows Out of office.
- [x] Show as Free on a Radicale event, then check that another CalDAV client shows it as free.
- [x] Each value looks distinct in Day, Week, Month and Agenda, in light, dark and high contrast. Check the hatched bar, the dashed and dotted borders, and both glyphs.
- [x] New event: timed starts as Busy and all-day as Free. Toggling All day switches it until you pick a value yourself.
- [x] A Radicale event set to Tentative in Outlook shows Tentative, offers Busy and Free, and keeps Tentative unless you change it.
- [x] A repeating event: changing show-as asks for the scope.

## Out of scope

- Free/busy lookup of other people.
- Changing show-as on meetings where the user is only an attendee (read-only, per [calendar-views](calendar-views.md) R9).

## Acceptance criteria

- [x] Setting Out of office in the app shows as Out of office in Outlook.
- [x] Setting Free on a Radicale event writes `TRANSP:TRANSPARENT`, and another CalDAV client shows it as free.
- [x] Each show-as value is visually distinct in Day, Week, Month and Agenda views, in both themes.

## Dependencies

- [sync-ms365](sync-ms365.md), [sync-radicale](sync-radicale.md), [calendar-views](calendar-views.md), [recurring-events](recurring-events.md), [ui-colour-theme](ui-colour-theme.md)
