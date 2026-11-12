---
description: Plain-text location/address on events that syncs to both backends, with no map or geocoding
when-to-use: Before working on the event location field or location sync mapping
tags: [events, location, ui]
status: closed
---

# Event location

## Summary

Events have a location field where the user can type a place name or address. It syncs to both backends. Maps, geocoding and autocomplete are deliberately excluded. Coordinates are never created by the app, but any coordinates already present from the server are preserved.

## Requirements

- R1. Event editor has a single-line "Location" text field (free text, e.g. "12 Main St, Sydney NSW 2000").
- R2. Location text is shown in the event details and, space permitting, in the Day/Week event block.
- R3. Mapping:
  - CalDAV: `LOCATION` property. When the user edits the text, remove `GEO` and `X-APPLE-STRUCTURED-LOCATION`, which no longer match.
  - Graph: `location.displayName`. When the user edits the text, set `displayName` and clear any structured `address` and `coordinates` that no longer match.
- R4. Preservation: if the user does not touch the location, existing server-side location data (Graph `address`/`coordinates`/`locations[]`; CalDAV `GEO`, `X-APPLE-STRUCTURED-LOCATION`) is round-tripped unchanged. This includes offline edits to other fields.
- R5. Graph events with a structured address but no displayName show the formatted address as the location text.
- R6. Location text is included in search (see [search](search.md)).
- R7. Offline: location is plain text and fully available offline.

## Interim (current build)

- Built: R1, R2, R3, R4, R5 and R7. R6 is delivered by [search](search.md) R1.
- R4: an edit writes only the fields that changed, so an untouched `LOCATION` keeps its parameters. Properties marked `VALUE=URI` (e.g. `X-APPLE-STRUCTURED-LOCATION`) are written back unchanged, without escaped commas.
- R5: the address is formatted as "street, city state postal code, country", leaving out empty parts.

## Out of scope

- Map preview, map tiles, geocoding, address autocomplete, travel time.
- "Open in maps" link (may be added later).
- Room/resource booking (Graph rooms).

## Acceptance criteria

- [x] Location entered in the app appears in Outlook and in another CalDAV client.
- [x] An event with GEO / Graph coordinates created elsewhere keeps them after its title is edited in the app.
- [x] Location text is searchable.

## Dependencies

- [calendar-views](calendar-views.md), [sync-ms365](sync-ms365.md), [sync-radicale](sync-radicale.md), [search](search.md)
