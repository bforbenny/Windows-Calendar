---
description: Plain-text location/address on events that syncs to both backends, with no map or geocoding
when-to-use: Before working on the event location field or location sync mapping
tags: [events, location, ui]
status: pending
---

# Event location

## Summary

Events have a location field where the user can type a place name or address. It syncs to both backends. Maps, geocoding and autocomplete are deliberately excluded. Coordinates are never created by the app, but any coordinates already present from the server are preserved.

## Requirements

- R1. Event editor has a single-line "Location" text field (free text, e.g. "12 Main St, Sydney NSW 2000").
- R2. Location text is shown in the event details and, space permitting, in the Day/Week event block.
- R3. Mapping:
  - CalDAV: `LOCATION` property.
  - Graph: `location.displayName`. When the user edits the text, set `displayName` and clear any structured `address` and `coordinates` that no longer match.
- R4. Preservation: if the user does not touch the location, existing server-side location data (Graph `address`/`coordinates`/`locations[]`; CalDAV `GEO`, `X-APPLE-STRUCTURED-LOCATION`) is round-tripped unchanged. This includes offline edits to other fields.
- R5. Graph events with a structured address but no displayName show the formatted address as the location text.
- R6. Location text is included in search (see [search](search.md)).
- R7. Offline: location is plain text and fully available offline.

## Out of scope

- Map preview, map tiles, geocoding, address autocomplete, travel time.
- "Open in maps" link (may be added later).
- Room/resource booking (Graph rooms).

## Acceptance criteria

- [ ] Location entered in the app appears in Outlook and in another CalDAV client.
- [ ] An event with GEO / Graph coordinates created elsewhere keeps them after its title is edited in the app.
- [ ] Location text is searchable.

## Dependencies

- [calendar-views](calendar-views.md), [sync-ms365](sync-ms365.md), [sync-radicale](sync-radicale.md), [search](search.md)
