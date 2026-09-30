namespace NovaGet.Core.Engine;

/// <summary>Engine-wide state shared by every running download.</summary>
internal sealed record JobServices(SpeedLimits SpeedLimits, HostConnectionLimits HostLimits);
