using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace WindowTrace.Storage;

public sealed record SessionMetadata(
    Guid SessionId,
    DateTimeOffset StartedUtc,
    string OsDescription,
    string OsVersion,
    int OsBuild,
    string Runtime,
    string ProcessArchitecture,
    long QpcFrequency,
    string ApplicationVersion)
{
    public static async Task WriteAsync(
        string path,
        Guid sessionId,
        DateTimeOffset startedUtc,
        long qpcFrequency,
        CancellationToken cancellationToken = default)
    {
        var assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        var metadata = new SessionMetadata(
            sessionId,
            startedUtc,
            RuntimeInformation.OSDescription,
            Environment.OSVersion.Version.ToString(),
            Environment.OSVersion.Version.Build,
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            qpcFrequency,
            assemblyVersion);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
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
}
