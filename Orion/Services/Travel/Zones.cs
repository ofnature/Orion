using System;

namespace Orion.Services.Travel;

/// <summary>
/// A zone as the log says it: "territory 1189 (Yak T'el)". The id stays, because it is what
/// the code and the sheets use; the name is what anyone reading the log knows the zone by.
/// </summary>
public static class Zones
{
    /// <summary>Set by the plugin from the TerritoryType sheet. Unset (tests), the id alone.</summary>
    public static Func<uint, string?>? Name { get; set; }

    public static string Describe(uint territoryId)
        => Name?.Invoke(territoryId) is { Length: > 0 } name ? $"territory {territoryId} ({name})" : $"territory {territoryId}";
}
