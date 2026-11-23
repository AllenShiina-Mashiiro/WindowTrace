using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace WindowTrace.Storage;

public sealed record SessionMetadata(
    Guid SessionId,
    DateTimeOffset StartedUtc,
    string OsDescription,
    string OsVersion,
    int OsBuild,
    string? WindowsDisplayVersion,
    string? WindowsBuildNumber,
    int? WindowsUpdateRevision,
    string? WindowsEdition,
    string Runtime,
    string ProcessArchitecture,
    long QpcFrequency,
    uint? TargetProcessId,
    string? TargetProcessName,
    string ApplicationVersion)
{
    public static async Task WriteAsync(
        string path,
        Guid sessionId,
        DateTimeOffset startedUtc,
        long qpcFrequency,
        uint? targetProcessId = null,
        string? targetProcessName = null,
        CancellationToken cancellationToken = default)
    {
        const string key = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        var assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        var metadata = new SessionMetadata(
            sessionId,
            startedUtc,
            RuntimeInformation.OSDescription,
            Environment.OSVersion.Version.ToString(),
            Environment.OSVersion.Version.Build,
            ReadRegistryString(key, "DisplayVersion"),
            ReadRegistryString(key, "CurrentBuildNumber"),
            ReadRegistryInt(key, "UBR"),
            ReadRegistryString(key, "EditionID"),
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            qpcFrequency,
            targetProcessId,
            targetProcessName,
            assemblyVersion);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        await using var stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            16 * 1024,
            FileOptions.Asynchronous);

        await JsonSerializer.SerializeAsync(
            stream,
            metadata,
            new JsonSerializerOptions { WriteIndented = true },
            cancellationToken);
    }

    private static string? ReadRegistryString(string key, string valueName)
    {
        try { return Registry.GetValue(key, valueName, null)?.ToString(); }
        catch { return null; }
    }

    private static int? ReadRegistryInt(string key, string valueName)
    {
        try
        {
            var value = Registry.GetValue(key, valueName, null);
            return value is null ? null : Convert.ToInt32(value);
        }
        catch { return null; }
    }
}
