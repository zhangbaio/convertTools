namespace ShortDrama.Infrastructure.Parsing;

internal static class ProjectInfoLineParser
{
    internal static int FindSeparatorIndex(string line)
    {
        var colonIndex = line.IndexOf(':');
        var fullWidthColonIndex = line.IndexOf('：');

        if (colonIndex < 0)
        {
            return fullWidthColonIndex;
        }

        if (fullWidthColonIndex < 0)
        {
            return colonIndex;
        }

        return Math.Min(colonIndex, fullWidthColonIndex);
    }
}
