namespace OpenIGI.Game.Igi1.Ai;

/// <summary>
/// How a soldier's suspicion of a human fills while it can see him and drains while it cannot: the modern AI's gate between
/// the original's sight test and the soldier believing it. <b>Not retail</b> (<see cref="GameplayCVars.ModernAi"/>).
/// </summary>
/// <remarks>
/// Full is one. A soldier holding a human in plain view, near and dead ahead, fills in under a second, and one standing still
/// fourteen metres off in about three; at the far end of its cone, or at the cone's rim, crouched and still, it may take half a
/// minute -- long enough to get out of the way. A soldier hunting for someone fills twice as fast.
/// </remarks>
public static class AiSuspicion
{
    /// <summary>What full suspicion fills at, per second, in the best view: near, dead ahead, standing, walking.</summary>
    // ponytail: hand-tuned constants; move to a settings row per AI type once play-testing settles them.
    public const double FillPerSecond = 1.2;

    /// <summary>What a soldier's suspicion drains at, per second, once it cannot see the human.</summary>
    public const double DrainPerSecond = 0.25;

    /// <summary>How much faster an alerted soldier fills: it is already looking for someone.</summary>
    public const double AlertedFactor = 2.0;

    /// <summary>The least time anyone takes from nothing to sure, in seconds: the player always gets a moment to react.</summary>
    /// <remarks>Without it a soldier a footstep had alerted, close and facing the player, went from nothing to sure in one scan.</remarks>
    public const double FastestSeconds = 0.6;

    /// <summary>The least share of the fill a human anywhere in a cone gives, so the far end is slow, not never.</summary>
    public const double FloorViewStrength = 0.1;

    /// <summary>The least share for an alerted soldier, who is looking for someone: far off, he still makes him out in seconds.</summary>
    public const double AlertedFloorViewStrength = 0.3;

    /// <summary>How near, in metres, a soldier senses a human outside his cone, with nothing between them.</summary>
    public const double CloseMetres = 2.0;

    /// <summary>The share of the fill sensing a human so close gives: about three seconds at a walk, eight crouched and still.</summary>
    public const double CloseViewStrength = 0.3;

    /// <summary>What crouching multiplies the fill by.</summary>
    public const double Crouched = 0.6;

    /// <summary>Below this speed, in metres a second, a human counts as still.</summary>
    public const double StillBelowMetresPerSecond = 0.3;

    /// <summary>Above this speed, in metres a second, a human counts as running.</summary>
    public const double RunningAboveMetresPerSecond = 3.0;

    /// <summary>Moves suspicion on by one stretch of time.</summary>
    /// <param name="suspicion">The suspicion before, nought to one.</param>
    /// <param name="sees">Whether the original's cone and sight ray both pass for the human.</param>
    /// <param name="viewStrength">How squarely the human sits in view (<see cref="OpenIGI.Game.Ai.AiEyeSnapshot.ViewStrength"/>).</param>
    /// <param name="crouching">Whether the human is crouched.</param>
    /// <param name="metresPerSecond">How fast the human is moving.</param>
    /// <param name="alerted">Whether the soldier is already alerted.</param>
    /// <param name="seconds">How long has passed.</param>
    /// <returns>The suspicion after, nought to one.</returns>
    public static double Step(double suspicion, bool sees, double viewStrength, bool crouching, double metresPerSecond, bool alerted, double seconds)
    {
        if (!sees)
        {
            return Math.Max(0.0, suspicion - (DrainPerSecond * seconds));
        }

        double movement = metresPerSecond < StillBelowMetresPerSecond ? 0.6
            : metresPerSecond > RunningAboveMetresPerSecond ? 1.4
            : 1.0;
        double rate = FillPerSecond
            * Math.Max(alerted ? AlertedFloorViewStrength : FloorViewStrength, viewStrength)
            * (crouching ? Crouched : 1.0)
            * movement
            * (alerted ? AlertedFactor : 1.0);
        return Math.Min(1.0, suspicion + (Math.Min(rate, 1.0 / FastestSeconds) * seconds));
    }
}
