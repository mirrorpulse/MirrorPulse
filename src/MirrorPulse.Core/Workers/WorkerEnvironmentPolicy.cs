namespace MirrorPulse.Core.Workers;

/// <summary>Builds a minimal environment without inheriting Host secrets or runtime injection variables.</summary>
public static class WorkerEnvironmentPolicy
{
    public static IReadOnlyDictionary<string, string> Create(WorkerLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string system = Path.Combine(windows, "System32");
        string temporary = Path.Combine(request.WorkingDirectory, "temporary");
        if (request.Environment.TryGetValue("MP_TRANSFER_CACHE_DIR", out string? cache))
        {
            temporary = Path.Combine(Path.GetFullPath(cache), "temporary");
        }
        else
        {
            temporary = Path.GetTempPath();
        }

        Directory.CreateDirectory(temporary);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = windows,
            ["WINDIR"] = windows,
            ["SystemDrive"] = Path.GetPathRoot(windows)!.TrimEnd(Path.DirectorySeparatorChar),
            ["COMSPEC"] = Path.Combine(system, "cmd.exe"),
            ["PATH"] = string.Join(Path.PathSeparator, system, windows),
            ["TEMP"] = temporary,
            ["TMP"] = temporary,
        };
        foreach (var pair in request.Environment)
        {
            if (pair.Key is not ("MP_TRANSFER_CACHE_DIR" or "MP_FILE_CACHE_DIR"))
            {
                throw new InvalidDataException("The Worker environment contains an undeclared variable.");
            }

            result[pair.Key] = Path.GetFullPath(pair.Value);
        }

        return result;
    }
}
