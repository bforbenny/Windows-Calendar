using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml.Controls;
using WindowsCalendar.Core.Store;
using WindowsCalendar.Views;

namespace WindowsCalendar;

/// <summary>
/// Joining online meetings (online-meeting-join R4, R6): Ctrl+J joins the selected event; Ctrl+Shift+J joins the
/// meeting on now or starting within 10 minutes, with a picker when there are several. Cards show Join only in that
/// window, so the views redraw on the minute when an event enters or leaves it.
/// </summary>
public sealed partial class MainWindow
{
    private HashSet<string> _joinShown = [];

    private void JoinSelected()
    {
        if (Selected() is { Join: { } link })
        {
            MeetingJoin.Launch(link);
        }
    }

    private void JoinNext()
    {
        var now = DateTimeOffset.Now;
        var candidates = (Store?.GetOccurrenceViews(now.AddDays(-2), now.AddMinutes(DisplayEvent.JoinLeadMinutes + 1)) ?? [])
            .Where(o => o.Kind == ItemKind.Event && _collections.ContainsKey(o.CollectionId))
            .Select(o => DisplayEvent.From(o, _collections[o.CollectionId]))
            .Where(e => e.JoinNow(DateTime.Now))
            .OrderBy(e => e.Start)
            .ToList();
        if (candidates.Count == 0)
        {
            ShowUndo("No meeting to join", canUndo: false, "Nothing with a join link is on now or starting in the next 10 minutes.");
            return;
        }
        if (candidates.Count == 1)
        {
            MeetingJoin.Launch(candidates[0].Join!);
            return;
        }
        var picker = new MenuFlyout();
        foreach (var e in candidates)
        {
            var item = new MenuFlyoutItem { Text = $"{e.Title} · {e.TimeText()} · {e.Join!.Provider}" };
            var link = e.Join;
            item.Click += (_, _) => MeetingJoin.Launch(link);
            picker.Items.Add(item);
        }
        picker.ShowAt(ViewHost);
    }

    /// <summary>Remembers which shown events have Join now (called after each render).</summary>
    private void RememberJoinShown() => _joinShown = JoinableNow();

    /// <summary>Whether an event entered or left its Join window since the last render.</summary>
    private bool JoinChanged() => !JoinableNow().SetEquals(_joinShown);

    private HashSet<string> JoinableNow()
    {
        var now = DateTime.Now;
        return _shownEvents.Where(e => e.JoinNow(now)).Select(e => e.Key).ToHashSet();
    }
}
