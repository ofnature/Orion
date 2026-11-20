using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orion.Services.Fleet;

/// <summary>
/// One box's line in the fleet list. What every Orion publishes on Daedalus's LAN relay every
/// ~2s; nothing here is time-critical. (Odysseus's record, with Orion's fields.)
///
/// <para>
/// <b>Extend-only.</b> New fields may be added; existing ones never change meaning or type. Older
/// peers ignore what they do not know (<see cref="JsonSerializerOptions"/> below is lenient), so
/// a fleet on mixed versions still draws every row.
/// </para>
/// </summary>
public sealed class FleetStatus
{
    /// <summary>Stable per-character key: <c>Name@World</c>.</summary>
    public string SenderId { get; set; } = string.Empty;
    public string Character { get; set; } = string.Empty;
    public string World { get; set; } = string.Empty;

    /// <summary>Whether a run is driving the character.</summary>
    public bool Running { get; set; }

    /// <summary>What kind of run: "Hunt Log" or "Trophy Hunt". Empty when idle.</summary>
    public string Mode { get; set; } = string.Empty;

    /// <summary>What it is after: the mob of the hunt log target, or the item and count.</summary>
    public string Goal { get; set; } = string.Empty;

    /// <summary>How far along, as the tiles read it ("2 / 3"). Empty when there is nothing to count.</summary>
    public string Progress { get; set; } = string.Empty;

    /// <summary>The run's status line, so "skipping X" or "travelling to Y" travels with the row.</summary>
    public string StatusLine { get; set; } = string.Empty;

    /// <summary>Where the character is, by zone name.</summary>
    public string Zone { get; set; } = string.Empty;

    /// <summary>Sender's clock, unix ms. Used for ordering only; staleness is judged by receipt time.</summary>
    public long SentUnixMs { get; set; }

    /// <summary>Schema marker for the extend-only rule.</summary>
    public int Version { get; set; } = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        // Unknown fields from a newer sender are simply dropped.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Null on anything unparseable — a bad frame is dropped, never thrown.</summary>
    public static FleetStatus? FromJson(string json)
    {
        try
        {
            var s = JsonSerializer.Deserialize<FleetStatus>(json, Json);
            return s is { SenderId.Length: > 0 } ? s : null;
        }
        catch
        {
            return null;
        }
    }
}
