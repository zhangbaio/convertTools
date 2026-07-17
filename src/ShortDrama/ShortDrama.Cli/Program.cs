using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ShortDrama.Cli.Commands;
using ShortDrama.Infrastructure.DependencyInjection;
using System.CommandLine;

return await ProgramMainAsync(args);

static async Task<int> ProgramMainAsync(string[] args)
{
    var services = new ServiceCollection();
    ConfigureServices(services);

    using var serviceProvider = services.BuildServiceProvider();
    var rootCommand = BuildRootCommand(serviceProvider);

    return await rootCommand.InvokeAsync(args);
}

static void ConfigureServices(IServiceCollection services)
{
    services.AddLogging(logging =>
    {
        logging.ClearProviders();
        logging.AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "HH:mm:ss ";
        });
    });

    services.AddShortDramaServices();

    services.AddSingleton<CostReportBuildCommand>();
    services.AddSingleton<BatchFileRenameCommand>();
    services.AddSingleton<ParseInfoCommand>();
    services.AddSingleton<PosterRenameCommand>();
    services.AddSingleton<ProjectResumeCommand>();
    services.AddSingleton<ProjectInfoRewriteCommand>();
    services.AddSingleton<ProjectImageGenerateCommand>();
    services.AddSingleton<TranscodeBatchCommand>();
    services.AddSingleton<WorkScanCommand>();
    services.AddSingleton<WorkflowRunCommand>();
    services.AddSingleton<WeixinUploadCommand>();
}

static RootCommand BuildRootCommand(IServiceProvider services)
{
    var root = new RootCommand("ShortDrama tools");
    root.AddCommand(services.GetRequiredService<CostReportBuildCommand>().Create());
    root.AddCommand(services.GetRequiredService<BatchFileRenameCommand>().Create());
    root.AddCommand(services.GetRequiredService<ParseInfoCommand>().Create());
    root.AddCommand(services.GetRequiredService<PosterRenameCommand>().Create());
    root.AddCommand(services.GetRequiredService<ProjectResumeCommand>().Create());
    root.AddCommand(services.GetRequiredService<ProjectInfoRewriteCommand>().Create());
    root.AddCommand(services.GetRequiredService<ProjectImageGenerateCommand>().Create());
    root.AddCommand(services.GetRequiredService<TranscodeBatchCommand>().Create());
    root.AddCommand(services.GetRequiredService<WorkScanCommand>().Create());
    root.AddCommand(services.GetRequiredService<WorkflowRunCommand>().Create());
    root.AddCommand(services.GetRequiredService<WeixinUploadCommand>().Create());
    return root;
}
