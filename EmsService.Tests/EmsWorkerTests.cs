using EmsService.Configuration;
using EmsService.Hosting;
using EmsService.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;

namespace EmsService.Tests;

/// <summary>
/// Test cases W01-W05 (see TEST-CASES.md).
/// </summary>
public class EmsWorkerTests : IAsyncDisposable
{
    private const string Group = "g";

    private readonly TestWorkspace _ws = new();

    private readonly EmsSettingsStore _store;

    private readonly EmsWorker _worker;

    public EmsWorkerTests()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string> { ["EmsSettingsFilePath"] = "emssettings.json" })
            .Build();
        _store = new EmsSettingsStore(configuration, new HostingEnvironment { ContentRootPath = _ws.Root },
            NullLogger<EmsSettingsStore>.Instance);
        _worker = new EmsWorker(_store, NullLoggerFactory.Instance, NullLogger<EmsWorker>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _worker.StopAsync(CancellationToken.None);
        _worker.Dispose();
        _ws.Dispose();
    }

    private async Task<RootConfiguration> SaveSettingsAsync(Action<RootConfiguration> setup = null)
    {
        var settings = _ws.CreateConfiguration();
        settings.Groups[Group] = _ws.CreateGroup("a@example.com");
        setup?.Invoke(settings);
        await _store.SaveAsync(settings, TestContext.Current.CancellationToken);
        return settings;
    }

    private async Task<EmsWorkerStatus> WaitFinishedAsync()
    {
        var timeout = DateTime.UtcNow.AddSeconds(30);
        while (_worker.Status.IsRunning)
        {
            Assert.True(DateTime.UtcNow < timeout, "Run is not finished in time.");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        return _worker.Status;
    }

    [Fact]
    public async Task W01_NotRequested_NoRun()
    {
        await SaveSettingsAsync();

        await _worker.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(300, TestContext.Current.CancellationToken);

        Assert.False(_worker.Status.IsRunning);
        Assert.Null(_worker.Status.LastRunStartedAt);
        Assert.Empty(_ws.GetTaskFiles());
    }

    [Fact]
    public async Task W02_RunRequested_Completed()
    {
        var settings = await SaveSettingsAsync();
        await _worker.StartAsync(TestContext.Current.CancellationToken);

        Assert.True(_worker.RequestRun());
        Assert.True(_worker.Status.IsRunning);
        var status = await WaitFinishedAsync();

        Assert.Equal(EmsRunResult.Completed, status.LastRunResult);
        Assert.NotNull(status.LastRunFinishedAt);
        Assert.Null(status.LastError);
        Assert.NotNull(Assert.Single(_ws.ReadTask(Group, settings.Groups[Group]).Items).Attempt);
    }

    [Fact]
    public async Task W03_RunWhileRunning_RejectedAndStopped()
    {
        using var server = new FakeSmtpServer(FakeSmtpMode.Hang);
        await SaveSettingsAsync(s => s.SmtpClient = new SmtpClientConfiguration
            { Enable = true, Host = "127.0.0.1", Port = server.Port, UseSsl = false });
        await _worker.StartAsync(TestContext.Current.CancellationToken);

        Assert.True(_worker.RequestRun());
        Assert.False(_worker.RequestRun());
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.True(_worker.Status.IsRunning);

        Assert.True(_worker.RequestStop());
        var status = await WaitFinishedAsync();

        Assert.Equal(EmsRunResult.Stopped, status.LastRunResult);
    }

    [Fact]
    public async Task W04_StopWhenIdle_ReturnsFalse()
    {
        await _worker.StartAsync(TestContext.Current.CancellationToken);

        Assert.False(_worker.RequestStop());
    }

    [Fact]
    public async Task W05_ServiceError_Failed()
    {
        await SaveSettingsAsync(s => s.SmtpClient = null);
        await _worker.StartAsync(TestContext.Current.CancellationToken);

        _worker.RequestRun();
        var status = await WaitFinishedAsync();

        Assert.Equal(EmsRunResult.Failed, status.LastRunResult);
        Assert.False(string.IsNullOrEmpty(status.LastError));
        Assert.True(_worker.RequestRun(), "A new run can be requested after a failed one.");
    }
}
