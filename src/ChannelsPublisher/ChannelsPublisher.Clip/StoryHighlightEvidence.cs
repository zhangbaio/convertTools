namespace ChannelsPublisher.Clip;

internal static class StoryHighlightEvidence
{
    public static async Task<ClipSourceAnalysis[]> EnrichAsync(ClipSourceAnalysis[] sources, string root,
        ClipEngineOptions options, Action<string>? log, CancellationToken ct)
    {
        var result = new ClipSourceAnalysis[sources.Length];
        await Parallel.ForEachAsync(Enumerable.Range(0, sources.Length), new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(options.PipelineConcurrency, 1, 3), CancellationToken = ct,
        }, async (index, token) =>
        {
            var source = sources[index];
            var candidates = source.Candidates.Select(clip => ConfiguredClipPlanner.Part(clip, clip.StartMs, clip.EndMs)).ToList();
            result[index] = source with { Candidates = candidates };
            if (!options.StoryHardSubtitles)
                return;
            try
            {
                var hard = await ClipHardSubtitles.ReadAsync(source, root, options, token);
                var missing = ClipHardSubtitles.MissingDialogue(source.Dialogue, hard);
                var dialogue = source.Dialogue.Concat(missing).OrderBy(cue => cue.StartMs).ToArray();
                foreach (var cue in missing)
                {
                    candidates.Add(new ClipCandidate
                    {
                        VideoPath = source.Source.VideoPath, EpisodeIndex = source.Source.EpisodeIndex,
                        StartMs = cue.StartMs, EndMs = cue.EndMs, Text = cue.Text, Total = 2,
                    });
                }
                if (missing.Count > 0)
                    candidates.AddRange(CandidateBuilder.Build(source.Source, dialogue, source.Media.DurationMs)
                        .Where(clip => missing.Any(cue => cue.StartMs >= clip.StartMs && cue.EndMs <= clip.EndMs)));
                result[index] = result[index] with
                {
                    Dialogue = dialogue,
                    HardSubtitles = hard,
                };
                log?.Invoke($"第 {source.Source.EpisodeIndex} 集本地 OCR：识别 {hard.Count} 条稳定硬字幕，补入 {missing.Count} 条缺失对白。");
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                log?.Invoke($"第 {source.Source.EpisodeIndex} 集本地硬字幕未完成，保留原对白。{ex.Message}");
            }
        });
        if (options.StoryVisualReview)
        {
            try { await StoryVisualReview.ApplyAsync(result, options, log, ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log?.Invoke($"画面复评不可用，保留现有素材分析。{ex.Message}");
            }
        }
        return result;
    }
}
