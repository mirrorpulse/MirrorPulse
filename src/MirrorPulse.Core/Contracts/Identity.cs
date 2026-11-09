namespace MirrorPulse.Core.Contracts;

/// <summary>
/// Identifies an Adapter across package versions and installations.
/// </summary>
public readonly record struct AdapterId
{
    public AdapterId(string value)
    {
        if (!TryNormalize(value, out var normalized))
        {
            throw new ArgumentException("Adapter IDs must use lowercase letters, digits, dots, or hyphens.", nameof(value));
        }

        Value = normalized;
    }

    public string Value { get; }

    public static AdapterId Parse(string value) => new(value);

    public static bool TryParse(string? value, out AdapterId adapterId)
    {
        if (TryNormalize(value, out var normalized))
        {
            adapterId = new AdapterId(normalized);
            return true;
        }

        adapterId = default;
        return false;
    }

    public override string ToString() => Value;

    private static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim())
        {
            return false;
        }

        var candidate = value.ToLowerInvariant();
        if (candidate.Length > 128 || candidate[0] is '.' or '-' || candidate[^1] is '.' or '-')
        {
            return false;
        }

        foreach (var character in candidate)
        {
            if (character is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '.' and not '-')
            {
                return false;
            }
        }

        normalized = candidate;
        return true;
    }
}

/// <summary>
/// Identifies one installed copy of an Adapter package.
/// </summary>
public readonly record struct InstallId
{
    public InstallId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("An install ID cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public static InstallId New() => new(Guid.NewGuid());

    public static InstallId Parse(string value) => new(Guid.Parse(value));

    public static bool TryParse(string? value, out InstallId installId)
    {
        if (Guid.TryParse(value, out var guid) && guid != Guid.Empty)
        {
            installId = new InstallId(guid);
            return true;
        }

        installId = default;
        return false;
    }

    public override string ToString() => Value.ToString("D");
}

/// <summary>
/// Identifies one configured connection to an installed Adapter.
/// </summary>
public readonly record struct InstanceId
{
    [System.Text.Json.Serialization.JsonConstructor]
    public InstanceId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("An instance ID cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public static InstanceId New() => new(Guid.NewGuid());

    public static InstanceId Parse(string value) => new(Guid.Parse(value));

    public static bool TryParse(string? value, out InstanceId instanceId)
    {
        if (Guid.TryParse(value, out var guid) && guid != Guid.Empty)
        {
            instanceId = new InstanceId(guid);
            return true;
        }

        instanceId = default;
        return false;
    }

    public override string ToString() => Value.ToString("D");
}

/// <summary>
/// Identifies one Worker process session.
/// </summary>
public readonly record struct WorkerSessionId
{
    public WorkerSessionId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A Worker session ID cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public static WorkerSessionId New() => new(Guid.NewGuid());

    public static WorkerSessionId Parse(string value) => new(Guid.Parse(value));

    public static bool TryParse(string? value, out WorkerSessionId workerSessionId)
    {
        if (Guid.TryParse(value, out var guid) && guid != Guid.Empty)
        {
            workerSessionId = new WorkerSessionId(guid);
            return true;
        }

        workerSessionId = default;
        return false;
    }

    public override string ToString() => Value.ToString("D");
}

/// <summary>
/// Identifies one first-level Cloud Files directory registration.
/// </summary>
public readonly record struct RootId
{
    public RootId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A root ID cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public static RootId New() => new(Guid.NewGuid());

    public static RootId Parse(string value) => new(Guid.Parse(value));

    public static bool TryParse(string? value, out RootId rootId)
    {
        if (Guid.TryParse(value, out var guid) && guid != Guid.Empty)
        {
            rootId = new RootId(guid);
            return true;
        }

        rootId = default;
        return false;
    }

    public override string ToString() => Value.ToString("D");
}
