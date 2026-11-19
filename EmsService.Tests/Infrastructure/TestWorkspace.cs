using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmsService.Configuration;
using EmsService.Sending;

namespace EmsService.Tests.Infrastructure;

/// <summary>
/// Isolated temporary directory with a text file, task directory and configuration helpers.
/// </summary>
internal sealed class TestWorkspace : IDisposable
{
    public string Root { get; }

    public string TasksPath { get; }

    public string TextFilePath { get; }

    public TestWorkspace(string text = "Тестовое письмо")
    {
        Root = Path.Combine(Path.GetTempPath(), "ems-service-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        TasksPath = Path.Combine(Root, "tasks");
        TextFilePath = CreateTextFile("text.txt", text);
    }

    public string CreateTextFile(string name, string content)
    {
        var path = Path.Combine(Root, name);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    public RootConfiguration CreateConfiguration()
    {
        return new RootConfiguration
        {
            Threads = 1,
            TasksPath = TasksPath,
            FromAddress = "sender@example.com",
            FromTitle = "Sender",
            SmtpClient = new SmtpClientConfiguration { Enable = false },
            Groups = new Dictionary<string, GroupConfiguration>()
        };
    }

    public GroupConfiguration CreateGroup(params string[] receivers)
    {
        return new GroupConfiguration
        {
            PlainTextFileName = TextFilePath,
            Subject = "Test subject",
            Actuality = TimeSpan.FromHours(1),
            Receivers = receivers.ToList()
        };
    }

    public string GetTaskFilePath(string groupId, GroupConfiguration group)
    {
        var hex = Convert.ToHexString(MD5.HashData(File.ReadAllBytes(group.PlainTextFileName)));
        return Path.Combine(TasksPath, $"{groupId}_{hex}.task");
    }

    public EmailMassSendingTask ReadTask(string groupId, GroupConfiguration group)
    {
        // The service may still hold the file open for writing.
        using var file = new FileStream(GetTaskFilePath(groupId, group), FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite);
        return JsonSerializer.Deserialize<EmailMassSendingTask>(file)!;
    }

    public void WriteTask(string groupId, GroupConfiguration group, params EmailMassSendingTaskItem[] items)
    {
        Directory.CreateDirectory(TasksPath);
        var path = GetTaskFilePath(groupId, group);
        var task = new EmailMassSendingTask
        {
            TaskId = Path.GetFileName(path),
            Items = items.ToList()
        };
        File.WriteAllText(path, JsonSerializer.Serialize(task));
    }

    public string[] GetTaskFiles()
    {
        return Directory.Exists(TasksPath)
            ? Directory.GetFiles(TasksPath, "*.task").Select(Path.GetFileName).ToArray()!
            : Array.Empty<string>();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, true);
        }
        catch
        {
            // Best effort cleanup.
        }
    }
}
