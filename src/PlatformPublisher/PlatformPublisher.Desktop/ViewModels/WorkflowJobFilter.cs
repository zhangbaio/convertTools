namespace PlatformPublisher.Desktop.ViewModels;

public static class WorkflowJobFilter
{
    public static bool Matches(string? query, params string?[] fields)
    {
        if (string.IsNullOrWhiteSpace(query))
            return true;
        var text = query.Trim();
        foreach (var field in fields)
        {
            if (!string.IsNullOrWhiteSpace(field) &&
                field.Contains(text, StringComparison.CurrentCultureIgnoreCase))
                return true;
        }

        return false;
    }
}
