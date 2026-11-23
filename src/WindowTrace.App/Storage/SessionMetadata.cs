using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace WindowTrace.Storage;

public sealed record DisplayAdapterMetadata(
    string RegistryKey,
    string? Name,
    string? DriverVersion,
    string? DriverDate,
    string? Provider,
    string? MatchingDeviceId);

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
    string? CpuModel,
    string? CpuIdentifier,
    int LogicalProcessorCount,
    ulong? InstalledPhysicalMemoryBytes,
    string? SystemManufacturer,
    string? SystemProductName,
    string? BaseBoardProduct,
    string? BiosVersion,
    string? BiosReleaseDate,
    IReadOnlyList<DisplayAdapterMetadata> DisplayAdapters,
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
        const string osKey = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        const string cpuKey = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";
        const string biosKey = @"HARDWARE\DESCRIPTION\System\BIOS";
        var assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

        var metadata = new SessionMetadata(
            sessionId,
            startedUtc,
            RuntimeInformation.OSDescription,
            Environment.OSVersion.Version.ToString(),
            Environment.OSVersion.Version.Build,
            ReadRegistryString(osKey, "DisplayVersion"),
            ReadRegistryString(osKey, "CurrentBuildNumber"),
            ReadRegistryInt(osKey, "UBR"),
            ReadRegistryString(osKey, "EditionID"),
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            qpcFrequency,
            targetProcessId,
            targetProcessName,
            ReadLocalRegistryString(cpuKey, "ProcessorNameString"),
            ReadLocalRegistryString(cpuKey, "Identifier"),
            Environment.ProcessorCount,
            ReadInstalledMemoryBytes(),
            ReadLocalRegistryString(biosKey, "SystemManufacturer"),
            ReadLocalRegistryString(biosKey, "SystemProductName"),
            ReadLocalRegistryString(biosKey, "BaseBoardProduct"),
            ReadLocalRegistryString(biosKey, "BIOSVersion"),
            ReadLocalRegistryString(biosKey, "BIOSReleaseDate"),
            ReadDisplayAdapters(),
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
        try { return FormatRegistryValue(Registry.GetValue(key, valueName, null)); }
        catch { return null; }
    }

    private static string? ReadLocalRegistryString(string subKeyPath, string valueName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(subKeyPath, writable: false);
            return FormatRegistryValue(key?.GetValue(valueName));
        }
        catch { return null; }
    }

    private static string? FormatRegistryValue(object? value)
        => value switch
        {
            null => null,
            string text => text,
            string[] values => string.Join("; ", values),
            byte[] bytes => Convert.ToHexString(bytes),
            _ => value.ToString()
        };

    private static int? ReadRegistryInt(string key, string valueName)
    {
        try
        {
            var value = Registry.GetValue(key, valueName, null);
            return value is null ? null : Convert.ToInt32(value);
        }
        catch { return null; }
    }

    private static ulong? ReadInstalledMemoryBytes()
    {
        try
        {
            return GetPhysicallyInstalledSystemMemory(out var kilobytes)
                ? kilobytes * 1024UL
                : null;
        }
        catch { return null; }
    }

    private static IReadOnlyList<DisplayAdapterMetadata> ReadDisplayAdapters()
    {
        const string adapterClassPath = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
        var adapters = new List<DisplayAdapterMetadata>();

        try
        {
            using var classKey = Registry.LocalMachine.OpenSubKey(adapterClassPath, writable: false);
            if (classKey is null)
                return adapters;

            foreach (var name in classKey.GetSubKeyNames())
            {
                if (name.Length != 4 || !int.TryParse(name, out _))
                    continue;

                using var adapter = classKey.OpenSubKey(name, writable: false);
                var description = adapter?.GetValue("DriverDesc")?.ToString();
                if (string.IsNullOrWhiteSpace(description))
                    continue;

                adapters.Add(new DisplayAdapterMetadata(
                    name,
                    description,
                    adapter?.GetValue("DriverVersion")?.ToString(),
                    adapter?.GetValue("DriverDate")?.ToString(),
                    adapter?.GetValue("ProviderName")?.ToString(),
                    adapter?.GetValue("MatchingDeviceId")?.ToString()));
            }
        }
        catch
        {
            // Hardware inventory is optional; unavailable registry data is left null/empty.
        }

        return adapters;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalMemoryInKilobytes);
}
