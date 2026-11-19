using System.Text.Json;
using EmsService.Configuration;
using EmsService.Hosting;
using EmsService.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;

namespace EmsService.Tests;

/// <summary>
/// Test cases H01-H14 (see TEST-CASES.md).
/// </summary>
public class EmsSettingsStoreTests : IDisposable
{
    private readonly TestWorkspace _ws = new();

    private string SettingsPath => Path.Combine(_ws.Root, "emssettings.json");

    public void Dispose() => _ws.Dispose();

    private EmsSettingsStore CreateStore()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string> { ["EmsSettingsFilePath"] = "emssettings.json" })
            .Build();
        var environment = new HostingEnvironment { ContentRootPath = _ws.Root };
        return new EmsSettingsStore(configuration, environment, NullLogger<EmsSettingsStore>.Instance);
    }

    private RootConfiguration CreateValidSettings()
    {
        var settings = _ws.CreateConfiguration();
        settings.SmtpClient = new SmtpClientConfiguration { Enable = true, Host = "smtp.example.com" };
        settings.Groups["g"] = _ws.CreateGroup("a@example.com");
        return settings;
    }

    [Fact]
    public void H01_MissingFile_DefaultsWritten()
    {
        var store = CreateStore();

        Assert.Equal(SettingsPath, store.FilePath);
        Assert.True(File.Exists(SettingsPath));
        var settings = store.Get();
        Assert.Equal(1, settings.Threads);
        Assert.Equal("./tasks", settings.TasksPath);
        Assert.Empty(settings.Groups);
    }

    [Fact]
    public void H02_CorruptedFile_DefaultsUsedAndFileKept()
    {
        File.WriteAllText(SettingsPath, "{ not json");

        var store = CreateStore();

        Assert.Equal(1, store.Get().Threads);
        Assert.Equal("{ not json", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void H03_FileWithComments_Loaded()
    {
        File.WriteAllText(SettingsPath, """
            {
              // comment
              "Threads": 4,
              "FromTitle": "Отправитель",
            }
            """);

        var settings = CreateStore().Get();

        Assert.Equal(4, settings.Threads);
        Assert.Equal("Отправитель", settings.FromTitle);
    }

    [Fact]
    public async Task H04_Save_PersistedAndReloaded()
    {
        var store = CreateStore();
        var changed = 0;
        store.Changed += () => changed++;
        var settings = CreateValidSettings();
        settings.FromTitle = "Отправитель";

        await store.SaveAsync(settings, TestContext.Current.CancellationToken);

        Assert.Equal(1, changed);
        Assert.Contains("\"FromTitle\": \"Отправитель\"", File.ReadAllText(SettingsPath));
        Assert.False(File.Exists(SettingsPath + ".tmp"));
        var reloaded = CreateStore().Get();
        Assert.Equal("Отправитель", reloaded.FromTitle);
        Assert.Equal(new[] { "a@example.com" }, reloaded.Groups["g"].Receivers);
    }

    [Fact]
    public void H05_Get_ReturnsCopy()
    {
        var store = CreateStore();

        store.Get().Threads = 10;

        Assert.Equal(1, store.Get().Threads);
    }

    [Fact]
    public void H06_CamelCaseJson_Deserialized()
    {
        var settings = JsonSerializer.Deserialize<RootConfiguration>(
            """{"threads": 3, "smtpClient": {"host": "smtp.example.com"}}""", EmsSettingsStore.JsonOptions);

        Assert.Equal(3, settings!.Threads);
        Assert.Equal("smtp.example.com", settings.SmtpClient.Host);
    }

    [Fact]
    public void H07_Validate_ValidSettings_NoErrors()
    {
        Assert.Empty(EmsSettingsStore.Validate(CreateValidSettings()));
    }

    public static TheoryData<string, Action<RootConfiguration>> InvalidSettings => new()
    {
        { "threads", s => s.Threads = 0 },
        { "max count", s => s.RollUpStrategy = new RollUpStrategyConfiguration { Enable = true, MaxCount = 0 } },
        { "from address", s => s.FromAddress = "not an address" },
        { "encoding", s => s.Encoding = "no-such-encoding" },
        { "group encoding", s => s.Groups["g"].Encoding = "no-such-encoding" },
        { "group name", s => s.Groups["a;b"] = s.Groups["g"] },
        { "text file", s => s.Groups["g"].PlainTextFileName = " " },
        { "receiver", s => s.Groups["g"].Receivers.Add("nope") }
    };

    [Theory]
    [MemberData(nameof(InvalidSettings))]
    public void H08_Validate_InvalidSettings_Error(string name, Action<RootConfiguration> breakSettings)
    {
        var settings = CreateValidSettings();
        breakSettings(settings);

        Assert.True(EmsSettingsStore.Validate(settings).Count > 0, name);
    }

    [Fact]
    public void H09_Validate_IncompleteSettings_CanBeSaved()
    {
        // Default settings: SMTP is enabled, but the server and the sender are not set yet.
        var settings = CreateStore().Get();
        settings.Groups["g"] = _ws.CreateGroup("a@example.com");

        Assert.Empty(EmsSettingsStore.Validate(settings));
    }

    [Fact]
    public void H10_ValidateForRun_CompleteSettings_NoErrors()
    {
        Assert.Empty(EmsSettingsStore.ValidateForRun(CreateValidSettings()));
    }

    public static TheoryData<string, Action<RootConfiguration>> NotReadyForRun => new()
    {
        { "smtp host", s => s.SmtpClient.Host = null },
        { "from address", s => s.FromAddress = null },
        { "no groups", s => s.Groups.Clear() },
        { "invalid value", s => s.Threads = 0 },
        { "missing text file", s => s.Groups["g"].PlainTextFileName = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt") },
        { "no receivers", s => s.Groups["g"].Receivers.Clear() },
        { "unknown default group", s => s.DefaultGroups = "g;other" }
    };

    [Theory]
    [MemberData(nameof(NotReadyForRun))]
    public void H11_ValidateForRun_NotReady_Error(string name, Action<RootConfiguration> breakSettings)
    {
        var settings = CreateValidSettings();
        breakSettings(settings);

        Assert.True(EmsSettingsStore.ValidateForRun(settings).Count > 0, name);
    }

    [Fact]
    public void H13_ValidateForRun_EmptyText_Error()
    {
        var settings = CreateValidSettings();
        settings.Groups["g"].PlainTextFileName = _ws.CreateTextFile("empty.txt", "");

        Assert.Contains(EmsSettingsStore.ValidateForRun(settings), x => x.Contains("пуст"));
    }

    [Fact]
    public void H14_ValidateForRun_OnlySelectedGroupsChecked()
    {
        var settings = CreateValidSettings();
        settings.Groups["draft"] = _ws.CreateGroup();
        settings.Groups["draft"].PlainTextFileName = Path.Combine(_ws.Root, "missing.txt");
        settings.DefaultGroups = "G";

        Assert.Empty(EmsSettingsStore.ValidateForRun(settings));
    }

    [Fact]
    public void H12_ValidateForRun_SmtpDisabled_ServerAndSenderNotRequired()
    {
        var settings = CreateValidSettings();
        settings.SmtpClient = new SmtpClientConfiguration { Enable = false };
        settings.FromAddress = null;

        Assert.Empty(EmsSettingsStore.ValidateForRun(settings));
    }
}
