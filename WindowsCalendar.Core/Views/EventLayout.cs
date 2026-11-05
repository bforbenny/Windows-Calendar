namespace WindowsCalendar.Core.Views;

/// <summary>Placement maths for the views; pure functions, no UI.</summary>
public static class EventLayout
{
    /// <summary>
    /// Side-by-side columns for overlapping timed events in one day (calendar-views R4). Events that overlap,
    /// directly or through others, form a cluster; each event takes the first free column in its cluster, and
    /// every event in a cluster shares the cluster's column count. Returns one (Column, Columns) per input, in order.
    /// Intervals are half-open: an event ending at 10:00 does not overlap one starting at 10:00.
    /// </summary>
    public static (int Column, int Columns)[] Columns(IReadOnlyList<(double Start, double End)> events)
    {
        var result = new (int Column, int Columns)[events.Count];
        var order = Enumerable.Range(0, events.Count)
            .OrderBy(i => events[i].Start)
            .ThenByDescending(i => events[i].End)
            .ToList();

        var cluster = new List<int>();
        var columnEnds = new List<double>();
        var clusterEnd = double.MinValue;

        foreach (var i in order)
        {
            var (start, end) = events[i];
            if (start >= clusterEnd && cluster.Count > 0)
            {
                Close();
            }

            var column = columnEnds.FindIndex(e => e <= start);
            if (column < 0)
            {
                column = columnEnds.Count;
                columnEnds.Add(end);
            }
            else
            {
                columnEnds[column] = end;
            }
            result[i] = (column, 0);
            cluster.Add(i);
            clusterEnd = Math.Max(clusterEnd, end);
        }
        Close();
        return result;

        void Close()
        {
            foreach (var j in cluster)
            {
                result[j] = (result[j].Column, columnEnds.Count);
            }
            cluster.Clear();
            columnEnds.Clear();
            clusterEnd = double.MinValue;
        }
    }

    /// <summary>
    /// Rows for day spans in an all-day lane or a month week row (calendar-views R3): each span takes the first row
    /// that is free on all its days. Earlier spans first, then longer ones, then input order.
    /// Days are inclusive indices within the row. Returns one row per input, in order.
    /// </summary>
    public static int[] Lanes(IReadOnlyList<(int FirstDay, int LastDay)> spans)
    {
        var result = new int[spans.Count];
        var order = Enumerable.Range(0, spans.Count)
            .OrderBy(i => spans[i].FirstDay)
            .ThenByDescending(i => spans[i].LastDay - spans[i].FirstDay);

        var rowLastDay = new List<int>();
        foreach (var i in order)
        {
            var row = rowLastDay.FindIndex(last => last < spans[i].FirstDay);
            if (row < 0)
            {
                row = rowLastDay.Count;
                rowLastDay.Add(spans[i].LastDay);
            }
            else
            {
                rowLastDay[row] = spans[i].LastDay;
            }
            result[i] = row;
        }
        return result;
    }
}
