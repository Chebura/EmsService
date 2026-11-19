using System.Net.Mail;
using System.Text.Encodings.Web;
using System.Text.Json;
using EmsService.Configuration;

namespace EmsService.Hosting
{
    /// <summary>
    /// Keeps EMS settings in a JSON file. Settings are edited through the web UI.
    /// </summary>
    public class EmsSettingsStore
    {
        public static readonly JsonSerializerOptions JsonOptions = new()
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            // The web UI sends camelCase, the file is written in PascalCase.
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        private readonly object _sync = new();

        private readonly ILogger<EmsSettingsStore> _logger;

        private RootConfiguration _current;

        public string FilePath { get; }

        /// <summary>
        /// Raised after settings were saved.
        /// </summary>
        public event Action Changed;

        public EmsSettingsStore(IConfiguration configuration, IHostEnvironment environment,
            ILogger<EmsSettingsStore> logger)
        {
            _logger = logger;
            FilePath = Path.GetFullPath(configuration["EmsSettingsFilePath"] ?? "emssettings.json",
                environment.ContentRootPath);
            _current = Load();
        }

        /// <summary>
        /// Returns a copy of the current settings, so the caller can modify it freely.
        /// </summary>
        public RootConfiguration Get()
        {
            lock (_sync)
                return Clone(_current);
        }

        public async Task SaveAsync(RootConfiguration settings, CancellationToken cancellationToken)
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions);
            var tmp = FilePath + ".tmp";

            await File.WriteAllBytesAsync(tmp, body, cancellationToken);

            lock (_sync)
            {
                File.Move(tmp, FilePath, true);
                _current = Clone(settings);
            }

            _logger.LogInformation("Settings saved to `{path}`.", FilePath);

            Changed?.Invoke();
        }

        /// <summary>
        /// Checks that the entered values are correct. Incomplete settings (e.g. no SMTP server yet) can be saved,
        /// see <see cref="ValidateForRun"/>.
        /// </summary>
        public static IReadOnlyList<string> Validate(RootConfiguration settings)
        {
            var errors = new List<string>();

            if (settings.Threads < 1)
                errors.Add("Количество потоков должно быть больше 0.");

            if (settings.RollUpStrategy is { Enable: true, MaxCount: < 1 })
                errors.Add("Максимальное количество попыток должно быть больше 0.");

            if (!string.IsNullOrWhiteSpace(settings.FromAddress) &&
                !MailAddress.TryCreate(settings.FromAddress, out _))
                errors.Add($"Адрес отправителя `{settings.FromAddress}` некорректен.");

            foreach (var encoding in new[] { settings.Encoding }
                         .Concat(settings.Groups?.Values.Select(x => x?.Encoding) ?? []))
            {
                if (string.IsNullOrWhiteSpace(encoding))
                    continue;
                try
                {
                    System.Text.Encoding.GetEncoding(encoding);
                }
                catch (ArgumentException)
                {
                    errors.Add($"Неизвестная кодировка `{encoding}`.");
                }
            }

            foreach (var (name, group) in settings.Groups ?? new Dictionary<string, GroupConfiguration>())
            {
                // Group name is a part of the task file name and DefaultGroups uses `;` as a separator.
                if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                    name.Contains(';'))
                    errors.Add($"Некорректное имя группы `{name}`.");

                if (group == null)
                {
                    errors.Add($"Группа `{name}` пуста.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(group.PlainTextFileName))
                    errors.Add($"Группа `{name}`: не задан файл с текстом письма.");

                foreach (var receiver in group.Receivers ?? [])
                {
                    if (!MailAddress.TryCreate(receiver, out _))
                        errors.Add($"Группа `{name}`: некорректный адрес получателя `{receiver}`.");
                }
            }

            return errors;
        }

        /// <summary>
        /// Checks that the settings are complete enough to start sending.
        /// </summary>
        public static IReadOnlyList<string> ValidateForRun(RootConfiguration settings)
        {
            var errors = Validate(settings).ToList();

            if (settings.SmtpClient?.Enable != false)
            {
                if (string.IsNullOrWhiteSpace(settings.SmtpClient?.Host))
                    errors.Add("Не задан SMTP-сервер.");

                if (string.IsNullOrWhiteSpace(settings.FromAddress))
                    errors.Add("Не задан адрес отправителя.");
            }

            if (settings.Groups == null || settings.Groups.Count == 0)
            {
                errors.Add("Нет ни одной группы получателей.");
                return errors;
            }

            // The same group selection as in EmailMassSendingService.
            var selected = string.IsNullOrWhiteSpace(settings.DefaultGroups) || settings.DefaultGroups.Trim() == "*"
                ? settings.Groups.Keys.ToList()
                : settings.DefaultGroups.Trim().Split(';').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

            foreach (var name in selected)
            {
                var key = settings.Groups.Keys.FirstOrDefault(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
                if (key == null)
                {
                    errors.Add($"Группа `{name}` из списка групп для рассылки не найдена.");
                    continue;
                }

                var group = settings.Groups[key];
                if (group == null || string.IsNullOrWhiteSpace(group.PlainTextFileName))
                    continue; // Reported by Validate.

                if (!File.Exists(group.PlainTextFileName))
                    errors.Add($"Группа `{key}`: нет файла с текстом письма `{group.PlainTextFileName}`. Сохраните текст письма на вкладке «Группы».");
                else if (new FileInfo(group.PlainTextFileName).Length == 0)
                    errors.Add($"Группа `{key}`: текст письма пуст.");

                if (group.Receivers == null || group.Receivers.Count == 0)
                    errors.Add($"Группа `{key}`: нет получателей.");
            }

            return errors;
        }

        private RootConfiguration Load()
        {
            if (!File.Exists(FilePath))
            {
                var defaults = CreateDefault();
                File.WriteAllBytes(FilePath, JsonSerializer.SerializeToUtf8Bytes(defaults, JsonOptions));
                _logger.LogWarning("Settings file `{path}` not found. Default settings are written.", FilePath);
                return defaults;
            }

            try
            {
                var settings =
                    JsonSerializer.Deserialize<RootConfiguration>(File.ReadAllBytes(FilePath), JsonOptions);
                _logger.LogInformation("Settings loaded from `{path}`.", FilePath);
                return settings ?? CreateDefault();
            }
            catch (Exception e)
            {
                // Do not fail the host: settings can be fixed through the web UI.
                _logger.LogError(e, "Loading settings from `{path}` failed. Default settings are used.", FilePath);
                return CreateDefault();
            }
        }

        private static RootConfiguration CreateDefault() => new()
        {
            Threads = 1,
            TasksPath = "./tasks",
            DeleteObsoleteTasks = true,
            TaskFileLifetime = TimeSpan.FromDays(14),
            DelayBetweenSendsInThread = TimeSpan.FromSeconds(1),
            SmtpClient = new SmtpClientConfiguration { Enable = true, Port = 587, UseSsl = true },
            RollUpStrategy = new RollUpStrategyConfiguration
                { Enable = true, Delay = TimeSpan.FromMinutes(1), MaxCount = 300 },
            Groups = new Dictionary<string, GroupConfiguration>()
        };

        private static RootConfiguration Clone(RootConfiguration settings) =>
            JsonSerializer.Deserialize<RootConfiguration>(JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions),
                JsonOptions);
    }
}
