using System.Windows;
using System.Windows.Controls;
using WindowsCalendar.Core.Store;
using ContentDialog = Wpf.Ui.Controls.ContentDialog;
using ContentDialogButton = Wpf.Ui.Controls.ContentDialogButton;
using ContentDialogResult = Wpf.Ui.Controls.ContentDialogResult;
using InfoBar = Wpf.Ui.Controls.InfoBar;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;

namespace WindowsCalendar.Views;

/// <summary>
/// The editor's Join button (online-meeting-join R4): shown when the open event has a join link, read-only meetings
/// included. An MS365 occurrence without its own link uses its series'.
/// </summary>
internal sealed partial class EventEditor
{
    private readonly Button _join = new Wpf.Ui.Controls.Button
    {
        Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
        Visibility = Visibility.Collapsed,
    };
    private JoinLink? _joinLink;

    private void RefreshJoin()
    {
        var occurrence = _occurrenceId is { } o ? Store?.GetItem(o) : null;
        var item = _itemId is { } id ? Store?.GetItem(id) : null;
        _joinLink = JoinLink.Recognise(occurrence?.JoinUrl ?? item?.JoinUrl);
        _join.Visibility = _joinLink is null ? Visibility.Collapsed : Visibility.Visible;
        if (_joinLink is { } link)
        {
            _join.Content = $"Join {link.Provider} meeting";
            _join.ToolTip = "Ctrl+J";
        }
    }

    private void Join()
    {
        if (_joinLink is { } link)
        {
            MeetingJoin.Launch(link);
        }
    }
}
