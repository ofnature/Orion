namespace Orion.Services.Game;

/// <summary>Where a completed run takes the character. (Demeter's "go home" choice, copied.)</summary>
public enum RunHome
{
    Stay = 0,
    /// <summary>The Return action — your home point, on its own cooldown.</summary>
    Return = 1,
    /// <summary>Lifestream: the inn.</summary>
    Inn = 2,
    /// <summary>Lifestream: whichever estate its own preference names (/li auto).</summary>
    Estate = 3,
    /// <summary>Lifestream: the free company estate.</summary>
    FreeCompany = 4,
    /// <summary>Lifestream: your private estate.</summary>
    Private = 5,
    /// <summary>Lifestream: your apartment.</summary>
    Apartment = 6,
}

public static class RunHomeCommands
{
    /// <summary>The chat command that goes there, or null for staying put. Lifestream owns every trip but Return.</summary>
    public static string? For(RunHome home) => home switch
    {
        RunHome.Return => "/generalaction Return",
        RunHome.Inn => "/li inn",
        RunHome.Estate => "/li auto",
        RunHome.FreeCompany => "/li fc",
        RunHome.Private => "/li home",
        RunHome.Apartment => "/li apt",
        _ => null,
    };

    /// <summary>Whether the trip is Lifestream's to make (every one but Return).</summary>
    public static bool NeedsLifestream(RunHome home) => home is not (RunHome.Stay or RunHome.Return);

    public static string Label(RunHome home) => home switch
    {
        RunHome.Stay => "Stay where I am",
        RunHome.Return => "Return (home point)",
        RunHome.Inn => "Inn",
        RunHome.Estate => "Estate (Lifestream's preference)",
        RunHome.FreeCompany => "Free company estate",
        RunHome.Private => "Private estate",
        RunHome.Apartment => "Apartment",
        _ => home.ToString(),
    };
}
