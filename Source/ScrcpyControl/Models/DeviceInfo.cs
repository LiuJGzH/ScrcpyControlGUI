namespace ScrcpyControl.Models;

public sealed class DeviceInfo
{
    public required string Serial { get; init; }
    public required string State { get; init; }
    public string Model { get; init; } = string.Empty;
    public string Product { get; init; } = string.Empty;
    public string Device { get; init; } = string.Empty;
    public string TransportId { get; init; } = string.Empty;

    public string DisplayName =>
        string.IsNullOrWhiteSpace(Model) ? $"{Serial}  [{State}]" : $"{Model} · {Serial}  [{State}]";

    public bool IsUsable => string.Equals(State, "device", StringComparison.OrdinalIgnoreCase);

    public override string ToString() => DisplayName;
}
