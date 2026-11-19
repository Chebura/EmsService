using System.Text.Json.Serialization;
using EmsService.Sending;

namespace EmsService.Hosting
{
    [JsonConverter(typeof(JsonStringEnumConverter<EmsRunResult>))]
    public enum EmsRunResult
    {
        Completed,
        Failed,
        Stopped
    }

    public record EmsWorkerStatus(
        bool IsRunning,
        DateTimeOffset StartedAt,
        DateTimeOffset? LastRunStartedAt,
        DateTimeOffset? LastRunFinishedAt,
        EmsRunResult? LastRunResult,
        string LastError);

    /// <summary>
    /// Runs <see cref="EmailMassSendingService"/> when a run is requested from the web UI.
    /// A run lasts until all emails are sent or the attempts are exhausted, or until it is stopped.
    /// </summary>
    public class EmsWorker : BackgroundService
    {
        private readonly EmsSettingsStore _settings;

        private readonly ILoggerFactory _loggerFactory;

        private readonly ILogger<EmsWorker> _logger;

        private readonly SemaphoreSlim _runRequested = new(0, 1);

        private readonly object _sync = new();

        private CancellationTokenSource _currentRun;

        private volatile EmsWorkerStatus _status = new(false, DateTimeOffset.UtcNow, null, null, null, null);

        public EmsWorker(EmsSettingsStore settings, ILoggerFactory loggerFactory, ILogger<EmsWorker> logger)
        {
            _settings = settings;
            _loggerFactory = loggerFactory;
            _logger = logger;
        }

        public EmsWorkerStatus Status => _status;

        /// <summary>
        /// Requests a run. Returns false when a run is already in progress or requested.
        /// </summary>
        public bool RequestRun()
        {
            lock (_sync)
            {
                if (_status.IsRunning)
                    return false;

                // The run counts as started right away, so a repeated request or a stop request is handled
                // correctly before the worker picks the run up.
                _currentRun = new CancellationTokenSource();
                _status = _status with { IsRunning = true, LastRunStartedAt = DateTimeOffset.UtcNow };
                _runRequested.Release();
                return true;
            }
        }

        /// <summary>
        /// Cancels the current run. Returns false when nothing is running.
        /// </summary>
        public bool RequestStop()
        {
            lock (_sync)
            {
                if (_currentRun == null)
                    return false;

                _currentRun.Cancel();
                return true;
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("EMS worker started.");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await _runRequested.WaitAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                await RunOnceAsync(stoppingToken);
            }

            _logger.LogInformation("EMS worker stopped.");
        }

        private async Task RunOnceAsync(CancellationToken stoppingToken)
        {
            // A fresh copy for every run: the sending service mutates its configuration.
            var configuration = _settings.Get();

            using var stop = _currentRun;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, stop.Token);

            _logger.LogInformation("Sending run started.");

            EmsRunResult result;
            string error = null;

            try
            {
                var service = new EmailMassSendingService(configuration,
                    _loggerFactory.CreateLogger<EmailMassSendingService>());

                await service.SendAsync(cts.Token);

                result = cts.IsCancellationRequested ? EmsRunResult.Stopped : EmsRunResult.Completed;
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                result = EmsRunResult.Stopped;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Sending process error. UNDONE.");
                result = EmsRunResult.Failed;
                error = e.Message;
            }

            lock (_sync)
            {
                _currentRun = null;
                _status = _status with
                {
                    IsRunning = false,
                    LastRunFinishedAt = DateTimeOffset.UtcNow,
                    LastRunResult = result,
                    LastError = error
                };
            }

            _logger.LogInformation("Sending run finished: {result}.", result);
        }

        public override void Dispose()
        {
            _runRequested.Dispose();
            base.Dispose();
        }
    }
}
