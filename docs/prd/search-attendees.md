---
description: Attendee and organiser names and addresses are indexed so a person's name finds their meetings
when-to-use: Before working on attendee indexing, the FTS people column, or attendee suggestions for search filters
tags: [search, fts, attendees]
status: in-progress
---

# Search by attendee

## Summary

Search ([search](search.md)) does not find meetings by who is in them. This adds attendee and organiser names and email addresses to the FTS index, so typing a person's name or address finds their meetings, offline. It also provides the people lookup used by the attendee filter in [search-filters](search-filters.md).

## Requirements

- R1. A new FTS column `people` holds attendee and organiser display names and email addresses, from Graph `attendees` and `organizer`, and CalDAV `ATTENDEE` and `ORGANIZER` (CN and address).
  - Attendees are not stored as columns; they are extracted from `items.raw_payload` in `LocalStore.IndexItem`, in the same transaction as item writes.
- R2. The account's own address and name are not indexed, so typing your own name does not match every meeting. Matching of "own" is the same as `Declined.cs`.
- R3. Migration v22 (the next step after v21 in `Migrations.cs`) recreates `search_index` with the `people` column, because FTS5 cannot add a column in place. It sets the existing `build-search` meta flag, so `LocalStore.BuildSearchIndex` rebuilds the index once from existing items.
- R4. Plain search words also match `people`, so a name or address finds that person's meetings, offline.
- R5. A distinct-people lookup returns names and addresses from the `people` column, by prefix match, for the attendee filter suggestions in [search-filters](search-filters.md).
- R6. Results stay < 100 ms for ~10,000 items ([search](search.md) R8), checked by an automated performance test on ~10,000 items.

## Out of scope

- Storing attendees as structured columns.
- Directory lookup of people who are not in any synced item.

## Acceptance criteria

- [ ] An item written with Graph attendees/organizer or CalDAV ATTENDEE/ORGANIZER has those names and addresses in the `people` column, in the same transaction as the item write.
- [ ] The account's own name and address are not in `people`; searching your own name does not match every meeting.
- [ ] After upgrading to v22, the first start rebuilds the index once from existing items and `build-search` is cleared; the next start does not rebuild.
- [ ] Searching an attendee's or organiser's name or address finds their meetings, with the network off.
- [ ] The people lookup for a prefix returns the matching distinct names and addresses, and none that appear in no item.
- [ ] The performance test on ~10,000 items passes with search returning in < 100 ms.

## Dependencies

- [search](search.md), [attendees-invitations](attendees-invitations.md), [local-store-offline](local-store-offline.md)
