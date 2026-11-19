using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using MimeKit.Text;

namespace EmsService.Sending
{
    using Configuration;

    public class EmailMassSendingService
    {
        private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

        private static readonly TimeSpan MinRetryDelay = TimeSpan.FromSeconds(1);

        private readonly RootConfiguration _configuration;

        private readonly ILogger _logger;

        public EmailMassSendingService(RootConfiguration configuration, ILogger logger)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

            if (_configuration.Threads <= 0)
                _configuration.Threads = 1;

            if (configuration.RollUpStrategy == null)
                configuration.RollUpStrategy = new RollUpStrategyConfiguration
                {
                    Enable = false,
                    Delay = TimeSpan.Zero,
                    MaxCount = 1
                };

            _logger = logger;
        }

        /// <summary>
        /// Sends emails until every item is either sent or will never be processed again
        /// (attempts are exhausted or its lifetime is expired), or until cancellation.
        /// </summary>
        public async Task SendAsync(CancellationToken cancellationToken)
        {
            _logger.LogDebug("Default group `{0}` passed.", _configuration.DefaultGroups);

            var tasks = await GetTasksListAsync(_configuration.DefaultGroups ?? "*", cancellationToken);

            if (!(tasks?.Any() ?? false))
            {
                _logger.LogDebug("No tasks.");
                return;
            }

            if (_configuration.SmtpClient == null)
            {
                await CloseTasksAsync(tasks);
                throw new InvalidOperationException("There are no SmtpClient object in configuration. Sending failed.");
            }

            _logger.LogDebug($"Start processing tasks at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss tt zz}");

            var active = tasks.ToList();

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    foreach (var task in active.ToList())
                    {
                        try
                        {
                            using var scope = _logger.BeginScope(task.TaskId);
                            await ProcessTaskAsync(task, cancellationToken);
                            var s = task.Success ? "SUCCESS" : "ERROR";
                            _logger.LogInformation(
                                $"Task done with {s}. Total processed: {task.Total}, sent: {task.Sent}, failed: {task.Failed}");
                            if (task.Success)
                                active.Remove(task);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception e)
                        {
                            // A broken task would fail on every pass, so it is excluded from the run.
                            _logger.LogError(e, $"Processing task error (TaskId: {task.TaskId}). Task is skipped.");
                            active.Remove(task);
                        }
                    }

                    if (!active.Any())
                        break;

                    var next = active.Min(x => x.NextAttempt ?? DateTimeOffset.UtcNow);
                    var delay = next - DateTimeOffset.UtcNow;
                    if (delay < MinRetryDelay)
                        delay = MinRetryDelay;

                    _logger.LogDebug($"Items are waiting for retry. Next pass in {delay}.");
                    await Task.Delay(delay, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Overall loop error");
            }

            _logger.LogDebug($"End processing tasks at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss tt zz}");


            if (cancellationToken.IsCancellationRequested)
                _logger.LogDebug("Cancellation requested");

            await CloseTasksAsync(tasks);
        }

        private static async Task CloseTasksAsync(IEnumerable<EmailMassSendingTaskContext> tasks)
        {
            foreach (var task in tasks.Where(x => x.File != null))
            {
                try
                {
                    await task.File.DisposeAsync();
                }
                catch
                {
                }
            }
        }

        private async Task<IEnumerable<EmailMassSendingTaskContext>> GetTasksListAsync(
            string defaultGroups,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_configuration.Groups == null)
                return null;

            var result = new List<EmailMassSendingTaskContext>();

            if (string.IsNullOrWhiteSpace(defaultGroups))
                defaultGroups = "*";

            var useGroups = defaultGroups == "*"
                ? new List<string>()
                : defaultGroups.Trim().Split(";").ToList();

            foreach (var kvp in _configuration.Groups)
            {
                if (useGroups.Any() &&
                    !useGroups.Any(x => string.Equals(x, kvp.Key, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                _logger.LogDebug($"Group `{kvp.Key}` matched.");

                try
                {
                    var groupOfTasks = await PrepareGroupOfTasksAsync(kvp.Key, kvp.Value, _configuration.ResetTasks,
                        cancellationToken);

                    if (groupOfTasks != null)
                        result.Add(groupOfTasks);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, $"Error when processing group `{kvp.Key}`. Group will be skipped.");
                }
            }

            return result;
        }

        private async Task<EmailMassSendingTaskContext> PrepareGroupOfTasksAsync(string groupId,
            GroupConfiguration groupConfiguration, bool force, CancellationToken cancellationToken)
        {
            await using var file = new FileStream(groupConfiguration.PlainTextFileName, FileMode.Open, FileAccess.Read,
                FileShare.Read);

            var buffer = new byte[file.Length];

            await file.ReadExactlyAsync(buffer, cancellationToken);

            using var md5 = MD5.Create();

            var hex = Convert.ToHexString(md5.ComputeHash(buffer));

            var taskDir = _configuration.TasksPath ?? string.Empty;

            var taskId = $"{groupId}_{hex}.task";

            var taskPath = Path.Combine(taskDir, taskId);

            if (!Directory.Exists(taskDir))
                Directory.CreateDirectory(taskDir);
            else
            {
                if (_configuration.DeleteObsoleteTasks)
                {
                    var lifetime = _configuration.TaskFileLifetime ?? TimeSpan.FromDays(1);
                    foreach (var taskFileForDelete in Directory.GetFiles(taskDir, "*.task",
                                 SearchOption.TopDirectoryOnly))
                    {
                        var fi = new FileInfo(taskFileForDelete);
                        if (DateTime.Compare(fi.CreationTimeUtc.Add(lifetime), DateTime.UtcNow) < 0)
                        {
                            File.Delete(taskFileForDelete);
                            _logger.LogDebug($"Task file `{taskFileForDelete}` was deleted as obsolete.");
                        }
                    }
                }
            }

            if (File.Exists(taskPath))
            {
                if (!force)
                {
                    _logger.LogDebug($"Task `{taskId}` already exists. Reusing.");
                    var taskFs = new FileStream(taskPath, FileMode.Open, FileAccess.ReadWrite,
                        FileShare.Read);
                    return new EmailMassSendingTaskContext(taskId, taskFs, buffer, groupConfiguration);
                }

                File.Delete(taskPath);
            }

            var newTask = new EmailMassSendingTask
            {
                TaskId = taskId,
                Items = new List<EmailMassSendingTaskItem>()
            };

            if (groupConfiguration.Receivers == null)
            {
                _logger.LogWarning($"No receivers in group `{groupId}` specified.");
                return null;
            }

            foreach (var receiver in groupConfiguration.Receivers.Distinct())
            {
                var newTaskItem = new EmailMassSendingTaskItem
                {
                    Receiver = receiver,
                    Failed = false,
                    Failure = null,
                    Lifetime =
                        new DateTimeOffset(DateTime.UtcNow.Add(groupConfiguration.Actuality ?? TimeSpan.FromHours(1)))
                };

                if (_configuration.RollUpStrategy.Enable)
                {
                    newTaskItem.AttemptsLeft = _configuration.RollUpStrategy.MaxCount;
                    newTaskItem.WaitFor = DateTimeOffset.UtcNow;
                }
                else
                {
                    // Without the roll-up strategy every receiver gets exactly one attempt.
                    newTaskItem.AttemptsLeft = 1;
                }

                newTask.Items.Add(newTaskItem);
            }

            var task2Fs = new FileStream(taskPath, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.Read);

            var taskFileBody = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(newTask));

            task2Fs.Position = 0;
            task2Fs.SetLength(taskFileBody.Length);

            await task2Fs.WriteAsync(taskFileBody, 0, taskFileBody.Length, cancellationToken);

            task2Fs.Position = 0;

            return new EmailMassSendingTaskContext(taskId, task2Fs, buffer, groupConfiguration);
        }


        private async Task<EmailMassSendingTaskContext> ProcessTaskAsync(EmailMassSendingTaskContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _logger.LogTrace("Reading file");

            var buffer = new byte[context.File.Length];
            context.File.Position = 0;
            await context.File.ReadExactlyAsync(buffer, cancellationToken);

            var taskList = JsonSerializer.Deserialize<EmailMassSendingTask>(buffer);

            var taskItems = taskList!.Items ?? new List<EmailMassSendingTaskItem>();

            var allTaskItems = taskItems;

            taskItems = allTaskItems.Where(IsReadyToProcess).ToList();

            if (!taskItems.Any())
            {
                _logger.LogDebug($"No task items to process.");
                context.Success = IsCompleted(allTaskItems);
                context.NextAttempt = GetNextAttempt(allTaskItems);
                context.Total = 0;
                context.Sent = 0;
                context.Failed = 0;
                return context;
            }

            var partedTaskItems = new List<EmailMassSendingTaskItem>[_configuration.Threads];
            var threadsCount = _configuration.Threads;

            for (var i = 0; i < threadsCount; i++)
                partedTaskItems[i] = new List<EmailMassSendingTaskItem>();

            var q = new Queue<EmailMassSendingTaskItem>(taskItems);

            for (var i = 0; i < taskItems.Count; i++)
                partedTaskItems[i % threadsCount].Add(q.Dequeue());

            _logger.LogTrace("Begin parallel processing");

            try
            {
                await Parallel.ForEachAsync(partedTaskItems, new ParallelOptions
                    {
                        MaxDegreeOfParallelism = threadsCount,
                        CancellationToken = cancellationToken
                    },
                    async (b, ct) => await ProcessTaskItemsAsync(context, b, ct));

                _logger.LogTrace("Begin parallel processing done.");
            }
            finally
            {
                // Persist the results even when the run is stopped, so sent emails are not sent again.
                buffer = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(taskList, IndentedJson));

                context.File.Position = 0;
                context.File.SetLength(buffer.Length);

                await context.File.WriteAsync(buffer, CancellationToken.None);

                await context.File.FlushAsync(CancellationToken.None);

                _logger.LogTrace("Writing file done.");
            }

            context.Success = IsCompleted(allTaskItems);
            context.NextAttempt = GetNextAttempt(allTaskItems);
            context.Total = taskItems.Count;
            context.Sent = taskItems.Count(x => x.Attempt != null && !x.Failed);
            context.Failed = context.Total - context.Sent;

            return context;
        }

        private async Task ProcessTaskItemsAsync(EmailMassSendingTaskContext context,
            ICollection<EmailMassSendingTaskItem> items, CancellationToken cancellationToken)
        {
            if (!items.Any())
                return;

            _logger.LogTrace("Processing task `{taskId}` items", context.TaskId);

            var delay = _configuration.DelayBetweenSendsInThread ?? TimeSpan.Zero;

            var lastItem = items.Last();

            foreach (var item in items)
            {
                if (cancellationToken.IsCancellationRequested)
                    return;
                await ProcessTaskItemAsync(context, item, cancellationToken);
                if (item != lastItem)
                    await Task.Delay(delay, cancellationToken);
            }
        }

        private async Task ProcessTaskItemAsync(EmailMassSendingTaskContext context,
            EmailMassSendingTaskItem item, CancellationToken cancellationToken)
        {
            _logger.LogTrace("Processing task `{taskId}` item: {item}", context.TaskId, item.Receiver);

            item.AttemptsLeft--;
            item.Attempt = DateTimeOffset.UtcNow;

            var defaultEncoding = _configuration.Encoding == null
                ? Encoding.UTF8
                : Encoding.GetEncoding(_configuration.Encoding);

            var encoding = context.GroupConfiguration.Encoding == null
                ? defaultEncoding
                : Encoding.GetEncoding(context.GroupConfiguration.Encoding);

            // The task id scope is opened by the caller (SendAsync).
            if (_configuration.SmtpClient.Enable ?? true)
            {
                _logger.LogTrace("Sending to {item}", item.Receiver);
                try
                {
                    using var message = new MimeMessage();
                    message.From.Add(new MailboxAddress(defaultEncoding, _configuration.FromTitle,
                        _configuration.FromAddress));
                    message.To.Add(MailboxAddress.Parse(item.Receiver));
                    message.Headers.Replace(HeaderId.Subject, encoding, context.GroupConfiguration.Subject ?? "");

                    var body = new TextPart(TextFormat.Plain);
                    body.SetText(encoding, encoding.GetString(context.Text));
                    message.Body = body;

                    using var client = new SmtpClient();

                    var port = _configuration.SmtpClient.Port ?? 25;
                    var ts = DateTime.UtcNow;
                    await client.ConnectAsync(_configuration.SmtpClient.Host, port,
                        GetSecureSocketOptions(port), cancellationToken);

                    // As System.Net.Mail did: authenticate only if the server supports it.
                    if (!string.IsNullOrEmpty(_configuration.SmtpClient.Username) &&
                        client.Capabilities.HasFlag(SmtpCapabilities.Authentication))
                        await client.AuthenticateAsync(_configuration.SmtpClient.Username,
                            _configuration.SmtpClient.Password ?? "", cancellationToken);

                    await client.SendAsync(message, cancellationToken);
                    await client.DisconnectAsync(true, cancellationToken);
                    _logger.LogDebug(
                        $"Mail to `{item.Receiver}` sent ({DateTime.UtcNow.Subtract(ts).TotalSeconds} s).");
                    item.Failed = false;
                    item.Failure = null;
                }
                catch (Exception e)
                {
                    _logger.LogError(e,
                        $"Email sending error (item receiver: `{item.Receiver}`)");
                    item.Failed = true;
                    item.Failure = e.Message;
                    item.WaitFor = DateTimeOffset.UtcNow.Add(_configuration.RollUpStrategy.Delay);
                }
            }
            else
            {
                item.Failed = false;
                item.Failure = "Not sent (disabled)";
            }
        }

        /// <summary>
        /// SSL/TLS: implicit TLS on port 465, otherwise mandatory STARTTLS.
        /// </summary>
        private SecureSocketOptions GetSecureSocketOptions(int port)
        {
            if (!(_configuration.SmtpClient.UseSsl ?? true))
                return SecureSocketOptions.None;

            return port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;
        }

        /// <summary>
        /// All items are either sent or will never be processed again.
        /// Items waiting for the next retry attempt are not completed.
        /// </summary>
        private bool IsCompleted(IEnumerable<EmailMassSendingTaskItem> items)
        {
            return items.All(x => IsDead(x) || (!x.Failed && x.Attempt != null));
        }

        /// <summary>
        /// The earliest time when one of the not completed items can be processed again.
        /// </summary>
        private DateTimeOffset? GetNextAttempt(IEnumerable<EmailMassSendingTaskItem> items)
        {
            var pending = items.Where(x => !IsDead(x) && (x.Failed || x.Attempt == null)).ToList();
            if (!pending.Any())
                return null;

            return pending.Min(x => x.WaitFor ?? DateTimeOffset.UtcNow);
        }

        private bool IsReadyToProcess(EmailMassSendingTaskItem item)
        {
            if (item.Attempt != null && !item.Failed)
                return false;

            if (IsDead(item))
                return false;

            // Waiting for the next retry attempt.
            if (item.WaitFor != null && DateTimeOffset.Compare(item.WaitFor.Value, DateTimeOffset.UtcNow) > 0)
                return false;

            return true;
        }

        /// <summary>
        /// Item will never be processed again: attempts are exhausted or its lifetime is expired.
        /// </summary>
        private bool IsDead(EmailMassSendingTaskItem item)
        {
            if (item.AttemptsLeft <= 0)
                return true;

            if (DateTimeOffset.Compare(item.Lifetime, DateTimeOffset.UtcNow) < 0)
                return true;

            return false;
        }
    }
}