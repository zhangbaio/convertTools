using Microsoft.Extensions.Logging.Abstractions;
using ShortDrama.Core.Interfaces;
using ShortDrama.Infrastructure.AI;
using ShortDrama.Infrastructure.Files;
using ShortDrama.Infrastructure.Imaging;
using ShortDrama.Infrastructure.Parsing;
using ShortDrama.Infrastructure.Process;

namespace TikTokPublisher.Core.Queue;

internal static class QueueInfrastructureServices
{
    private static readonly Lazy<IProjectInfoRewriter> ProjectInfoRewriter = new(CreateProjectInfoRewriter);
    private static readonly Lazy<IPosterRenamer> PosterRenamer = new(CreatePosterRenamer);
    private static readonly Lazy<IProjectImageGenerator> ProjectImageGeneratorService = new(CreateProjectImageGenerator);

    public static IProjectInfoRewriter InfoRewriter => ProjectInfoRewriter.Value;
    public static IPosterRenamer Poster => PosterRenamer.Value;
    public static IProjectImageGenerator ProjectImages => ProjectImageGeneratorService.Value;

    private static IProjectInfoRewriter CreateProjectInfoRewriter()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        return new ProjectInfoRewriter(
            new TxtProjectInfoParser(),
            http,
            NullLogger<ProjectInfoRewriter>.Instance);
    }

    private static IPosterRenamer CreatePosterRenamer()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        return new PosterRenamer(
            new TxtProjectInfoParser(),
            new ExternalProcessRunner(),
            http,
            NullLogger<PosterRenamer>.Instance);
    }

    private static IProjectImageGenerator CreateProjectImageGenerator()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        return new ProjectImageGenerator(
            new ExternalProcessRunner(),
            new TxtProjectInfoParser(),
            http,
            NullLogger<ProjectImageGenerator>.Instance);
    }
}
