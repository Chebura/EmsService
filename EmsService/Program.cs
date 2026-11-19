using EmsService.Api;
using EmsService.Hosting;
using NLog;
using NLog.Web;

// Windows services start in %SystemRoot%\System32; relative paths (tasks, texts) must resolve next to the executable.
Directory.SetCurrentDirectory(AppContext.BaseDirectory);

// Required for non-UTF encodings (e.g. windows-1251) in Encoding.GetEncoding.
System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

// Log messages are partly in Russian; the Windows console uses an OEM code page by default.
Console.OutputEncoding = System.Text.Encoding.UTF8;

var logger = LogManager.Setup()
    .LoadConfigurationFromFile(Path.Combine(AppContext.BaseDirectory, "nlog.config"))
    .GetCurrentClassLogger();

try
{
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = args,
        ContentRootPath = AppContext.BaseDirectory
    });

    // Each call is a no-op unless the process runs under the corresponding service manager.
    builder.Host.UseWindowsService(options => options.ServiceName = "EmsService");
    builder.Host.UseSystemd();

    // Serves wwwroot from the project when the build output is started directly (not only in Development).
    // No-op for a published application, where wwwroot is next to the executable.
    builder.WebHost.UseStaticWebAssets();

    builder.Logging.ClearProviders();
    builder.Host.UseNLog();

    builder.Services.AddSingleton<EmsSettingsStore>();
    builder.Services.AddSingleton<EmsWorker>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<EmsWorker>());

    var app = builder.Build();

    app.UseDefaultFiles();
    app.UseStaticFiles(new StaticFileOptions
    {
        // Revalidate the UI files on every load, so an updated service never runs with a stale script.
        OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache"
    });
    app.MapEmsApi();

    logger.Info("Email Mass Sender service v.{version} is starting.",
        typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0");

    app.Run();
}
catch (Exception e)
{
    logger.Fatal(e, "Host terminated unexpectedly.");
    throw;
}
finally
{
    LogManager.Shutdown();
}
