using System.Reflection;
using System.Text;
using System.Text.Json;
using EmsService.Configuration;
using EmsService.Hosting;
using EmsService.Sending;
using NLog;
using NLog.Targets;

namespace EmsService.Api
{
    public record SettingsResponse(RootConfiguration Settings, bool SmtpPasswordSet, string FilePath);

    public record TaskSummary(
        string Name,
        DateTime Created,
        DateTime Modified,
        int Total,
        int Sent,
        int Failed,
        int Waiting,
        int Dead,
        string Error);

    public record GroupText(string FileName, string Encoding, bool Exists, string Text);

    public static class EmsApi
    {
        public static void MapEmsApi(this IEndpointRouteBuilder app)
        {
            var api = app.MapGroup("/api");

            api.MapGet("/status", (EmsWorker worker, EmsSettingsStore settings) => Results.Ok(new
            {
                Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(),
                worker.Status,
                SettingsFile = settings.FilePath
            }));

            api.MapPost("/run", (EmsWorker worker, EmsSettingsStore store, ILogger<EmsWorker> logger) =>
            {
                var errors = EmsSettingsStore.ValidateForRun(store.Get());
                if (errors.Count > 0)
                {
                    logger.LogWarning("Sending run rejected: {errors}", string.Join(" ", errors));
                    return Results.BadRequest(new { Errors = errors });
                }

                return worker.RequestRun() ? Results.Accepted() : Results.Conflict(Error("Рассылка уже выполняется."));
            });

            api.MapPost("/stop", (EmsWorker worker) =>
                worker.RequestStop() ? Results.Accepted() : Results.Conflict(Error("Рассылка не выполняется.")));

            api.MapGet("/settings", (EmsSettingsStore store) =>
            {
                var settings = store.Get();
                var passwordSet = !string.IsNullOrEmpty(settings.SmtpClient?.Password);
                // The password is never sent to the browser.
                if (settings.SmtpClient != null)
                    settings.SmtpClient.Password = null;
                return Results.Ok(new SettingsResponse(settings, passwordSet, store.FilePath));
            });

            api.MapPut("/settings", async (HttpRequest request, EmsSettingsStore store,
                ILogger<EmsSettingsStore> logger, CancellationToken ct) =>
            {
                RootConfiguration settings;
                try
                {
                    settings = await JsonSerializer.DeserializeAsync<RootConfiguration>(request.Body,
                        EmsSettingsStore.JsonOptions, ct);
                }
                catch (JsonException e)
                {
                    return Results.BadRequest(Error($"Некорректный формат данных: {e.Message}"));
                }

                if (settings == null)
                    return Results.BadRequest(Error("Пустые настройки."));

                Normalize(settings);

                // Null password means "keep the current one".
                if (settings.SmtpClient is { Password: null })
                    settings.SmtpClient.Password = store.Get().SmtpClient?.Password;

                var errors = EmsSettingsStore.Validate(settings);
                if (errors.Count > 0)
                {
                    logger.LogWarning("Settings are not saved: {errors}", string.Join(" ", errors));
                    return Results.BadRequest(new { Errors = errors });
                }

                await store.SaveAsync(settings, ct);
                return Results.NoContent();
            });

            api.MapGet("/groups/{name}/text", async (string name, EmsSettingsStore store, CancellationToken ct) =>
            {
                var settings = store.Get();
                if (!TryGetGroup(settings, name, out var group))
                    return Results.NotFound(Error($"Группа `{name}` не найдена."));

                var encoding = GetEncoding(settings, group);
                var exists = File.Exists(group.PlainTextFileName);
                var text = exists ? await File.ReadAllTextAsync(group.PlainTextFileName, encoding, ct) : null;

                return Results.Ok(new GroupText(group.PlainTextFileName, encoding.WebName, exists, text));
            });

            api.MapPut("/groups/{name}/text",
                async (string name, GroupText body, EmsSettingsStore store, CancellationToken ct) =>
                {
                    // Only files of saved groups can be written.
                    var settings = store.Get();
                    if (!TryGetGroup(settings, name, out var group))
                        return Results.NotFound(Error($"Группа `{name}` не найдена. Сохраните настройки."));

                    var encoding = GetEncoding(settings, group);
                    var directory = Path.GetDirectoryName(Path.GetFullPath(group.PlainTextFileName));
                    if (!string.IsNullOrEmpty(directory))
                        Directory.CreateDirectory(directory);

                    // No BOM: the sending service sends the file bytes as is.
                    await File.WriteAllBytesAsync(group.PlainTextFileName, encoding.GetBytes(body.Text ?? string.Empty),
                        ct);
                    return Results.NoContent();
                });

            api.MapGet("/tasks", (EmsSettingsStore store) =>
            {
                var dir = GetTasksPath(store);
                if (!Directory.Exists(dir))
                    return Results.Ok(Array.Empty<TaskSummary>());

                var now = DateTimeOffset.UtcNow;
                var result = Directory.GetFiles(dir, "*.task", SearchOption.TopDirectoryOnly)
                    .Select(path => Summarize(path, now))
                    .OrderByDescending(x => x.Modified)
                    .ToList();

                return Results.Ok(result);
            });

            api.MapGet("/tasks/{name}", (string name, EmsSettingsStore store) =>
            {
                if (!TryGetTaskPath(store, name, out var path))
                    return Results.NotFound(Error($"Задача `{name}` не найдена."));

                try
                {
                    return Results.Ok(ReadTask(path));
                }
                catch (Exception e)
                {
                    return Results.Problem(e.Message);
                }
            });

            api.MapDelete("/tasks/{name}", (string name, EmsSettingsStore store, EmsWorker worker) =>
            {
                if (worker.Status.IsRunning)
                    return Results.Conflict(Error("Нельзя удалять задачи во время рассылки."));

                if (!TryGetTaskPath(store, name, out var path))
                    return Results.NotFound(Error($"Задача `{name}` не найдена."));

                File.Delete(path);
                return Results.NoContent();
            });

            api.MapGet("/logs", (int? count) =>
            {
                var target = LogManager.Configuration?.FindTargetByName<MemoryTarget>("memory");
                var logs = target?.Logs.ToArray() ?? [];
                var take = Math.Clamp(count ?? 300, 1, logs.Length == 0 ? 1 : logs.Length);
                return Results.Ok(logs.TakeLast(take));
            });
        }

        private static object Error(string message) => new { Errors = new[] { message } };

        /// <summary>
        /// Cleans up values coming from the web form.
        /// </summary>
        private static void Normalize(RootConfiguration settings)
        {
            settings.Groups ??= new Dictionary<string, GroupConfiguration>();
            settings.SmtpClient ??= new SmtpClientConfiguration();

            foreach (var group in settings.Groups.Values.Where(x => x != null))
            {
                group.Receivers = (group.Receivers ?? [])
                    .Select(x => x?.Trim())
                    .Where(x => !string.IsNullOrEmpty(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (string.IsNullOrWhiteSpace(group.Encoding))
                    group.Encoding = null;
            }

            if (string.IsNullOrWhiteSpace(settings.Encoding))
                settings.Encoding = null;

            if (string.IsNullOrWhiteSpace(settings.DefaultGroups))
                settings.DefaultGroups = null;
        }

        private static bool TryGetGroup(RootConfiguration settings, string name, out GroupConfiguration group)
        {
            group = null;
            return settings.Groups != null && settings.Groups.TryGetValue(name, out group) && group != null &&
                   !string.IsNullOrWhiteSpace(group.PlainTextFileName);
        }

        private static Encoding GetEncoding(RootConfiguration settings, GroupConfiguration group)
        {
            var name = group.Encoding ?? settings.Encoding;
            return name == null ? Encoding.UTF8 : Encoding.GetEncoding(name);
        }

        private static string GetTasksPath(EmsSettingsStore store) =>
            Path.GetFullPath(store.Get().TasksPath ?? string.Empty);

        private static bool TryGetTaskPath(EmsSettingsStore store, string name, out string path)
        {
            path = null;
            if (string.IsNullOrWhiteSpace(name) || !name.EndsWith(".task", StringComparison.OrdinalIgnoreCase) ||
                name != Path.GetFileName(name))
                return false;

            path = Path.Combine(GetTasksPath(store), name);
            return File.Exists(path);
        }

        private static EmailMassSendingTask ReadTask(string path)
        {
            // The running service keeps task files open for writing.
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return JsonSerializer.Deserialize<EmailMassSendingTask>(file);
        }

        private static TaskSummary Summarize(string path, DateTimeOffset now)
        {
            var info = new FileInfo(path);
            try
            {
                var items = ReadTask(path)?.Items ?? [];
                var sent = items.Count(x => x.Attempt != null && !x.Failed);
                var dead = items.Count(x =>
                    !(x.Attempt != null && !x.Failed) && (x.AttemptsLeft <= 0 || x.Lifetime < now));
                var failed = items.Count(x => x.Failed);
                return new TaskSummary(info.Name, info.CreationTime, info.LastWriteTime, items.Count, sent, failed,
                    items.Count - sent - dead, dead, null);
            }
            catch (Exception e)
            {
                return new TaskSummary(info.Name, info.CreationTime, info.LastWriteTime, 0, 0, 0, 0, 0, e.Message);
            }
        }
    }
}
