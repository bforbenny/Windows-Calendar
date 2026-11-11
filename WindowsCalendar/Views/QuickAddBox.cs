using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using WindowsCalendar.Core.QuickAdd;
using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Views;

/// <summary>
/// The quick-add box (quick-add R1, R4, R4a): a text box with, under it, the suggestions for the word at the caret, a
/// preview of what the parser understood as field chips, an error line and a "More details" link. Enter saves, Ctrl+Enter
/// or the link opens the full editor, Esc closes the suggestions first and then asks the owner to close. In the toolbar
/// the details float in a popup and never push the toolbar; inside the quick-create popup (itself floating) they sit under the box.
/// </summary>
internal sealed class QuickAddBox : UserControl
{
    private readonly Wpf.Ui.Controls.TextBox _box = new() { PlaceholderText = "Quick add, e.g. Lunch fri 1pm #Work" };
    private readonly StackPanel _list = new() { Margin = new Thickness(0, 0, 0, 6) };
    private readonly WrapPanel _chips = new();
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0), Visibility = Visibility.Collapsed };
    private readonly Border _details = new() { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(8) };
    private readonly Popup? _popup;

    private QuickAddResult? _result;
    private IReadOnlyList<QuickAddSuggestion> _suggestions = [];
    private int _selected;
    // Up/Down moved through the list since it was last recomputed: Enter then inserts the highlighted item (R4a).
    private bool _navigated;
    // Esc closed the suggestions; they stay closed until the text changes.
    private bool _dismissed;
    // Enter was pressed on a phrase that can't be saved: the error shows even for an empty box.
    private bool _attempted;

    public QuickAddBox() : this(true)
    {
    }

    /// <param name="floating">True in the toolbar: the details float in a popup under the box.</param>
    public QuickAddBox(bool floating)
    {
        AutomationProperties.SetName(_box, "Quick add");
        _details.SetResourceReference(Border.BackgroundProperty, "SurfaceBaseBrush");
        _details.SetResourceReference(Border.BorderBrushProperty, "DividerBrush");
        _error.SetResourceReference(TextBlock.ForegroundProperty, "DestructiveBrush");

        var more = new Wpf.Ui.Controls.Button
        {
            Content = "More details",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Transparent,
            Focusable = false,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 4, 0, 0),
            ToolTip = "Open the full editor (Ctrl+Enter)",
        };
        more.Click += (_, _) => Submit(more: true);
        var body = new StackPanel();
        body.Children.Add(_list);
        body.Children.Add(_chips);
        body.Children.Add(_error);
        body.Children.Add(more);
        _details.Child = body;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(_box);
        if (floating)
        {
            _details.MinWidth = 360;
            _details.Margin = new Thickness(0, 4, 0, 0);
            _popup = new Popup
            {
                Child = _details,
                PlacementTarget = _box,
                Placement = PlacementMode.Bottom,
                StaysOpen = true,
                AllowsTransparency = true,
            };
            root.Children.Add(_popup);
        }
        else
        {
            _details.Margin = new Thickness(0, 8, 0, 0);
            Grid.SetRow(_details, 1);
            root.Children.Add(_details);
        }
        Content = root;

        _box.TextChanged += (_, _) =>
        {
            _dismissed = false;
            _attempted = false;
            _selected = 0;
            Refresh();
        };
        _box.SelectionChanged += (_, _) => Refresh();
        _box.GotKeyboardFocus += (_, _) => Refresh();
        _box.LostKeyboardFocus += (_, e) =>
        {
            if (_popup is not null && (e.NewFocus is not DependencyObject next || !IsWithin(next)))
            {
                _popup.IsOpen = false;
            }
        };
        _box.PreviewKeyDown += OnKeyDown;
    }

    /// <summary>Supplies the parser's options (targets, slot, task default) each time the phrase is parsed.</summary>
    public Func<QuickAddOptions>? Options { get; set; }

    /// <summary>Finds a target's collection for its colour marker in the suggestion list.</summary>
    public Func<string, Collection?>? CollectionOf { get; set; }

    /// <summary>Raised on Enter with a phrase that can be saved.</summary>
    public event Action<QuickAddResult>? Submitted;

    /// <summary>Raised on Ctrl+Enter or the "More details" link.</summary>
    public event Action<QuickAddResult>? MoreDetails;

    /// <summary>Raised on Esc when no suggestion list is open.</summary>
    public event Action? CloseRequested;

    public string Text => _box.Text;

    public void Clear()
    {
        _box.Text = "";
        _dismissed = false;
        _attempted = false;
        Refresh();
    }

    public void FocusBox(bool selectAll = true)
    {
        _box.Focus();
        if (selectAll)
        {
            _box.SelectAll();
        }
    }

    private bool IsWithin(DependencyObject element)
    {
        for (var e = element; e is not null; e = (e is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(e) : null) ?? LogicalTreeHelper.GetParent(e))
        {
            if (e == this || e == _details)
            {
                return true;
            }
        }
        return false;
    }

    private bool ListOpen => _suggestions.Count > 0 && !_dismissed;

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down or Key.Up when ListOpen:
                e.Handled = true;
                _selected = (_selected + (e.Key == Key.Down ? 1 : _suggestions.Count - 1)) % _suggestions.Count;
                _navigated = true;
                ShowList();
                break;
            case Key.Tab when ListOpen && Keyboard.Modifiers == ModifierKeys.None:
            case Key.Enter when ListOpen && _navigated && Keyboard.Modifiers == ModifierKeys.None:
                e.Handled = true;
                Accept(_selected);
                break;
            case Key.Enter:
                e.Handled = true;
                Submit(more: Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
                break;
            case Key.Escape when ListOpen:
                e.Handled = true;
                _dismissed = true;
                Refresh();
                break;
            case Key.Escape:
                e.Handled = true;
                CloseRequested?.Invoke();
                break;
        }
    }

    /// <summary>Replaces the word being completed with the suggestion and a space, the caret after it (R4a).</summary>
    private void Accept(int index)
    {
        var text = _box.Text;
        var s = _suggestions[index];
        var (offset, length) = s.Replace.GetOffsetAndLength(text.Length);
        var insert = s.Insert + " ";
        _box.Text = text[..offset] + insert + text[(offset + length)..];
        _box.CaretIndex = offset + insert.Length;
        _box.Focus();
    }

    private void Submit(bool more)
    {
        Refresh();
        if (_result is not { } result)
        {
            return;
        }
        if (more)
        {
            MoreDetails?.Invoke(result);
        }
        else if (result.Error is not null)
        {
            _attempted = true;
            Refresh();
        }
        else
        {
            Submitted?.Invoke(result);
        }
    }

    /// <summary>Parses the phrase and redraws the suggestions and preview; shows or hides the details.</summary>
    private void Refresh()
    {
        if (Options?.Invoke() is not { } options)
        {
            return;
        }
        var text = _box.Text;
        _result = QuickAddParser.Parse(text, DateTime.Now, options);
        _suggestions = _dismissed ? [] : QuickAddSuggest.Suggest(text, _box.CaretIndex, options);
        _selected = Math.Min(_selected, Math.Max(_suggestions.Count - 1, 0));
        _navigated = false;

        _chips.Children.Clear();
        foreach (var chip in QuickAddParser.Chips(_result))
        {
            var label = new TextBlock { Text = chip, Style = Theme.Style("CaptionTextBlockStyle"), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 300 };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            var border = new Border
            {
                Child = label,
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(0, 0, 4, 4),
            };
            border.SetResourceReference(Border.BackgroundProperty, "SurfaceRaisedBrush");
            border.SetResourceReference(Border.BorderBrushProperty, "DividerBrush");
            _chips.Children.Add(border);
        }
        var showError = _result.Error is not null && (text.Length > 0 || _attempted);
        _error.Text = showError ? _result.Error : "";
        _error.Visibility = showError ? Visibility.Visible : Visibility.Collapsed;
        ShowList();

        if (_popup is not null)
        {
            _popup.IsOpen = _box.IsKeyboardFocused && (text.Length > 0 || options.SlotStart is not null);
        }
    }

    private void ShowList()
    {
        _list.Children.Clear();
        _list.Visibility = ListOpen ? Visibility.Visible : Visibility.Collapsed;
        if (!ListOpen)
        {
            return;
        }
        for (var i = 0; i < _suggestions.Count; i++)
        {
            var index = i;
            var suggestion = _suggestions[i];
            var row = new DockPanel();
            if (suggestion.Target is { } target && CollectionOf?.Invoke(target.Id) is { } collection)
            {
                var marker = ShapeMarker.Create(collection.Shape, Theme.Calendar(collection.Colour).Swatch);
                marker.Margin = new Thickness(0, 0, 8, 0);
                DockPanel.SetDock(marker, Dock.Left);
                row.Children.Add(marker);
            }
            if (suggestion.Detail.Length > 0)
            {
                var detail = new TextBlock { Text = suggestion.Detail, Style = Theme.Style("CaptionTextBlockStyle"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
                detail.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                DockPanel.SetDock(detail, Dock.Right);
                row.Children.Add(detail);
            }
            var name = new TextBlock { Text = suggestion.Label, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            name.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            row.Children.Add(name);

            var item = new Border { Child = row, CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 3, 8, 3), Background = System.Windows.Media.Brushes.Transparent };
            if (index == _selected)
            {
                item.SetResourceReference(Border.BackgroundProperty, "SurfaceRaisedBrush");
            }
            item.PreviewMouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                Accept(index);
            };
            _list.Children.Add(item);
        }
    }
}
