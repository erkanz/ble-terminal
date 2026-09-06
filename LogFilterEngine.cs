namespace BLESerialTerminal;

internal sealed record LogFilterCriteria(
    string Text,
    string Category,
    string Device,
    string Characteristic,
    string Direction,
    bool ErrorsWarningsOnly)
{
    public static LogFilterCriteria Empty { get; } = new(string.Empty, "ALL", string.Empty, string.Empty, "ALL", false);
}

internal static class LogFilterEngine
{
    public static bool Matches(LogEntry entry, LogFilterCriteria criteria)
    {
        if (!string.IsNullOrWhiteSpace(criteria.Text))
        {
            string text = criteria.Text.Trim();
            bool found = entry.Message.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                         entry.Device.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                         entry.Characteristic.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                         entry.CategoryText.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                         entry.Direction.Contains(text, StringComparison.OrdinalIgnoreCase);
            if (!found)
                return false;
        }

        if (!string.IsNullOrWhiteSpace(criteria.Category) &&
            !criteria.Category.Equals("ALL", StringComparison.OrdinalIgnoreCase) &&
            !entry.CategoryText.Equals(criteria.Category, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(criteria.Device) &&
            !entry.Device.Contains(criteria.Device.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(criteria.Characteristic) &&
            !entry.Characteristic.Contains(criteria.Characteristic.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(criteria.Direction) &&
            !criteria.Direction.Equals("ALL", StringComparison.OrdinalIgnoreCase) &&
            !entry.Direction.Equals(criteria.Direction, StringComparison.OrdinalIgnoreCase))
            return false;

        if (criteria.ErrorsWarningsOnly && !entry.IsError && !entry.IsWarning &&
            entry.Category is not LogCategory.ERROR and not LogCategory.WARNING)
            return false;

        return true;
    }

    public static IReadOnlyList<LogEntry> Filter(IEnumerable<LogEntry> entries, LogFilterCriteria criteria) =>
        entries.Where(entry => Matches(entry, criteria)).ToArray();
}
