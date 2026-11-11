---
description: Every in-app banner floats over the calendar as a card, so banners never push the views or toolbar
when-to-use: Before adding or changing any in-app banner, notice bar or InfoBar (undo, conflicts, offline, sign-in, sync errors)
tags: [ui, banners, infobar, layout]
status: closed
---

# Floating banners

## Summary

Banners appear and disappear often (after every drag, on going offline, on a conflict). If they are laid out under the toolbar, the calendar jumps down and back each time. Every in-app banner instead floats over the calendar as a card with a shadow, so nothing in the layout ever moves.

## Requirements

- R1. Every in-app banner floats: the undo banner ([undo](undo.md) R3), the conflicts banner ([conflict-resolution](conflict-resolution.md) R5) and the notice bars of [ui-states](ui-states.md) R2 (offline, sign-in needed, sync error), plus any banner added later. Showing or hiding one never moves the toolbar, the views or the editor panel.
- R2. Placement: one stack, centred at the bottom of the area below the toolbar (the views and the editor panel), 24 epx from the edges, 320–520 epx wide. Lasting conditions (offline, sign-in, sync error, conflicts) sit above the passing ones (undo), so the newest short-lived banner is nearest the pointer's usual area and lasting ones do not jump when it goes.
- R3. Look: the WPF-UI `InfoBar` on the solid surface colour ([ui-colour-theme](ui-colour-theme.md)) with the theme shadow, keeping its severity icon and colour, so it reads clearly over event cards in light, dark and high contrast.
- R4. Only the cards take clicks; the calendar around and behind the stack stays usable (click, drag, scroll) while banners show.
- R5. Lifetimes are unchanged: lasting banners stay until their condition clears or the user closes them (where closable); passing ones close on their own (undo: 8 seconds). The stack shrinks from the bottom up without moving the cards above.
- R6. Banners are announced to screen readers when they appear, and their buttons (Undo, Sign in, Retry, Review, close) are reachable with Tab ([keyboard-navigation](keyboard-navigation.md)).
- R7. Banners appear and disappear with a short fade, which is off when Windows animation effects are off ([ui-style](ui-style.md) R6).

## Out of scope

- Windows toast notifications ([reminders-notifications](reminders-notifications.md)); they are outside the window.
- Dialogs that need an answer (WPF-UI content dialog), which stay modal.

## Acceptance criteria

- [x] Dragging an event shows "Event moved · Undo" floating at the bottom centre; the week grid does not move when it appears or disappears.
- [x] With a conflict and an undo banner both showing, the conflict card sits above the undo card; when undo closes, the conflict card stays where it was.
- [x] Events under and beside the banner stack can still be clicked and dragged.
- [x] The cards are legible over event cards in light, dark and high contrast.

## Dependencies

- [ui-states](ui-states.md), [undo](undo.md), [conflict-resolution](conflict-resolution.md), [ui-colour-theme](ui-colour-theme.md), [ui-style](ui-style.md)
