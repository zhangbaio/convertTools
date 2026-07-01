using Microsoft.Extensions.DependencyInjection;
using ShortDrama.Core.Interfaces;
using ShortDrama.Infrastructure.AI;
using ShortDrama.Infrastructure.Automation;
using ShortDrama.Infrastructure.Automation.Weixin;
using ShortDrama.Infrastructure.Automation.Weixin.Pages;
using ShortDrama.Infrastructure.Config;
using ShortDrama.Infrastructure.Files;
using ShortDrama.Infrastructure.Imaging;
using ShortDrama.Infrastructure.Media;
using ShortDrama.Infrastructure.Office;
using ShortDrama.Infrastructure.Parsing;
using ShortDrama.Infrastructure.Process;
using ShortDrama.Infrastructure.Notifications;
using ShortDrama.Infrastructure.Workflow;
using System.Net;
using System.Net.Http.Headers;

namespace ShortDrama.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddShortDramaServices(this IServiceCollection services)
    {
        services.AddSingleton(_ =>
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                ConnectTimeout = TimeSpan.FromSeconds(15)
            };

            return new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(60),
                DefaultRequestVersion = HttpVersion.Version11,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
            };
        });
        services.AddSingleton<IBatchFileRenamer, BatchFileRenamer>();
        services.AddSingleton<IProjectInfoParser, TxtProjectInfoParser>();
        services.AddSingleton<IProjectScanner, ProjectScanner>();
        services.AddSingleton<IProjectArchiveService, ProjectArchiveService>();
        services.AddSingleton<IArchivedProjectDeleteService, ArchivedProjectDeleteService>();
        services.AddSingleton<IMaterialValidationService, MaterialValidationService>();
        services.AddSingleton<IWorkService, WorkService>();
        services.AddSingleton<PythonToolResolver>();
        services.AddSingleton<IWorkflowInteractionService, WorkflowInteractionService>();
        services.AddSingleton<IDramaSearchService, HongguoDramaSearchService>();
        services.AddSingleton<IDramaProjectBootstrapper, DramaProjectBootstrapper>();
        services.AddSingleton<IDramaDownloader, HongguoDramaDownloader>();
        services.AddSingleton<IWeixinAutomationConfigLoader, WeixinAutomationConfigLoader>();
        services.AddSingleton<WeixinBrowserRuntimeService>();
        services.AddSingleton<IWeixinBrowserRuntimeService>(provider => provider.GetRequiredService<WeixinBrowserRuntimeService>());
        services.AddSingleton<IWeixinAuthStateService, WeixinAuthStateService>();
        services.AddSingleton<IWeixinBrowserSessionLauncher, WeixinBrowserSessionLauncher>();
        services.AddSingleton<IExternalWeixinCliLocator, ExternalWeixinCliLocator>();
        services.AddSingleton<WeixinHomePage>();
        services.AddSingleton<WeixinSeriesSubmissionPage>();
        services.AddSingleton<WeixinMaterialPublishPage>();
        services.AddSingleton<IWeixinChannelUploader, WeixinChannelUploader>();
        services.AddSingleton<IProjectInfoRewriter, ProjectInfoRewriter>();
        services.AddSingleton<IPosterRenamer, PosterRenamer>();
        services.AddSingleton<IConfigLocator, UpwardConfigLocator>();
        services.AddSingleton<IExternalProcessRunner, ExternalProcessRunner>();
        services.AddSingleton<IProjectImageGenerator, ProjectImageGenerator>();
        services.AddSingleton<IVideoTranscoder, FfmpegVideoTranscoder>();
        services.AddSingleton<IVideoMaterialConverter, FfmpegVideoMaterialConverter>();
        services.AddSingleton<ICostReportTemplateService, OpenXmlCostReportTemplateService>();
        services.AddSingleton<ICostReportBuilder, CostReportBuilder>();
        services.AddSingleton<IWorkflowDefinitionLoader, JsonWorkflowDefinitionLoader>();
        services.AddSingleton<IWorkflowRunner, WorkflowRunner>();
        services.AddSingleton<IFeishuNotificationService, FeishuNotificationService>();
        services.AddSingleton<IWeixinLoginNotificationService, NoopWeixinLoginNotificationService>();

        return services;
    }
}
