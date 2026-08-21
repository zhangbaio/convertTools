namespace TikTokPublisher.Core.Drama;

public static class BoundPasswordAssigner
{
    public const string KickValue = " ";

    public static IReadOnlyList<string> AssignmentSteps(string? current, string? target)
    {
        var from = current ?? "";
        var to = target ?? "";
        var steps = new List<string>(2);
        if (string.Equals(from, to, StringComparison.Ordinal))
        {
            steps.Add(to.Length == 0 ? KickValue : "");
        }
        else if (from.Length == 0)
        {
            steps.Add(KickValue);
        }

        steps.Add(to);
        return steps;
    }
}
