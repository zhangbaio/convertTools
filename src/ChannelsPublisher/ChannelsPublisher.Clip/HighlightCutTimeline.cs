namespace ChannelsPublisher.Clip;

// Speech protection follows the timeline approach in DramaClip v0.2.5 (MIT):
// https://github.com/efarsoft/DramaClip/blob/v0.2.5/service/dramaclip/engines/dedup/jitter.py
internal sealed class HighlightCutTimeline
{
    private const int ShotSearchMs = 300;
    private readonly int _durationMs;
    private readonly IReadOnlyList<int> _shots;
    private readonly (int Start, int End)[] _speech;

    public HighlightCutTimeline(ClipSourceAnalysis source)
    {
        _durationMs = source.Media.DurationMs;
        _shots = source.ShotCuts;
        Dialogue = source.Dialogue;
        var spans = new List<(int Start, int End)>();
        foreach (var line in Dialogue.OrderBy(line => line.StartMs))
        {
            var start = Math.Clamp(line.StartMs, 0, _durationMs);
            var end = Math.Clamp(line.EndMs, start, _durationMs);
            if (start == end)
                continue;
            if (spans.Count > 0 && start <= spans[^1].End)
                spans[^1] = (spans[^1].Start, Math.Max(spans[^1].End, end));
            else
                spans.Add((start, end));
        }
        _speech = spans.ToArray();
    }

    public IReadOnlyList<SubtitleSegment> Dialogue { get; }

    public (int Start, int End)? Cover(ClipCandidate candidate)
    {
        var start = BeforeSpeech(Math.Clamp(candidate.StartMs, 0, _durationMs));
        var end = AfterSpeech(Math.Clamp(candidate.EndMs, 0, _durationMs));
        if (end <= start || candidate.StartMs - start > ShotSearchMs || end - candidate.EndMs > ShotSearchMs)
            return null;

        return (NearbyShot(start, Math.Max(0, candidate.StartMs - ShotSearchMs), start) ?? start,
            NearbyShot(end, end, Math.Min(_durationMs, candidate.EndMs + ShotSearchMs)) ?? end);
    }

    public (int Start, int End)? Fit(int left, int right, int preferredLength, int maximumLength, int minimumLength)
    {
        var start = AfterSpeech(left);
        start = NearbyShot(start, start, Math.Min(right - minimumLength, start + ShotSearchMs)) ?? start;
        var latestEnd = Math.Min(right, start + maximumLength);
        var earliestEnd = start + minimumLength;
        if (latestEnd < earliestEnd)
            return null;

        var desired = Math.Clamp(start + preferredLength, earliestEnd, latestEnd);
        var end = BeforeSpeech(desired);
        if (end < earliestEnd)
            end = AfterSpeech(earliestEnd);
        if (end > latestEnd)
            return null;

        end = NearbyShot(end, Math.Max(earliestEnd, end - ShotSearchMs), Math.Min(latestEnd, end + ShotSearchMs)) ?? end;
        return (start, end);
    }

    private int? NearbyShot(int point, int left, int right)
    {
        int? nearest = null;
        foreach (var shot in _shots)
        {
            if (shot < left || shot > right || (nearest.HasValue && Math.Abs(shot - point) >= Math.Abs(nearest.Value - point)))
                continue;
            var from = Math.Min(point, shot);
            var to = Math.Max(point, shot);
            if (!IntersectsSpeech(from, to))
                nearest = shot;
        }
        return nearest;
    }

    private bool IntersectsSpeech(int left, int right)
    {
        var index = LastSpeechBefore(right);
        return index >= 0 && _speech[index].End > left;
    }

    private int BeforeSpeech(int time)
    {
        var index = LastSpeechBefore(time);
        return index >= 0 && time < _speech[index].End ? _speech[index].Start : time;
    }

    private int AfterSpeech(int time)
    {
        var index = LastSpeechBefore(time);
        return index >= 0 && time < _speech[index].End ? _speech[index].End : time;
    }

    private int LastSpeechBefore(int time)
    {
        var left = 0;
        var right = _speech.Length;
        while (left < right)
        {
            var middle = left + (right - left) / 2;
            if (_speech[middle].Start < time)
                left = middle + 1;
            else
                right = middle;
        }
        return left - 1;
    }
}
