namespace WindowsCalendar.Core.Store;

/// <summary>How an event blocks your availability (event-show-as R1).</summary>
public enum ShowAs
{
    Busy,
    Free,
    Tentative,
    OutOfOffice,
    WorkingElsewhere,
}

/// <summary>
/// Show-as in Graph's vocabulary (<c>busy</c>, <c>free</c>, <c>tentative</c>, <c>oof</c>, <c>workingElsewhere</c>),
/// which is also how the store keeps it for both backends. Anything else, Graph's <c>unknown</c> included, reads as
/// Busy (R7).
/// </summary>
public static class ShowAsValues
{
    public static ShowAs FromGraph(string? value) => value switch
    {
        "free" => ShowAs.Free,
        "tentative" => ShowAs.Tentative,
        "oof" => ShowAs.OutOfOffice,
        "workingElsewhere" => ShowAs.WorkingElsewhere,
        _ => ShowAs.Busy,
    };

    public static string ToGraph(ShowAs value) => value switch
    {
        ShowAs.Free => "free",
        ShowAs.Tentative => "tentative",
        ShowAs.OutOfOffice => "oof",
        ShowAs.WorkingElsewhere => "workingElsewhere",
        _ => "busy",
    };

    /// <summary>The editor's label.</summary>
    public static string Label(ShowAs value) => value switch
    {
        ShowAs.Free => "Free",
        ShowAs.Tentative => "Tentative",
        ShowAs.OutOfOffice => "Out of office",
        ShowAs.WorkingElsewhere => "Working elsewhere",
        _ => "Busy",
    };
}
