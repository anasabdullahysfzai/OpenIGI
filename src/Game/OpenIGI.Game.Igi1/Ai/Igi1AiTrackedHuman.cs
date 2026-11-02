using OpenIGI.Game.Ai;

namespace OpenIGI.Game.Igi1.Ai;

/// <summary>One human the scan has been offered, and the slot it keeps for it.</summary>
/// <param name="candidate">How the human described itself the first time it was offered.</param>
internal sealed class Igi1AiTrackedHuman(AiDetectionCandidate candidate)
{
    /// <summary>Gets or sets how the human described itself this tick.</summary>
    public AiDetectionCandidate Candidate { get; set; } = candidate;

    /// <summary>Gets the detection slot the scan keeps for the human.</summary>
    public AiDetectionSlot Slot { get; } = new();

    /// <summary>Gets or sets what the original's sight test last said, before the modern AI's suspicion gate.</summary>
    public bool SeesRaw { get; set; }

    /// <summary>Gets or sets how sure the soldier is that someone is there, nought to one (<see cref="AiSuspicion"/>).</summary>
    public double Suspicion { get; set; }

    /// <summary>Gets or sets where the human stood at the last scan, which its speed is measured from.</summary>
    public OpenIGI.Core.Numerics.Vector3d? LastFeet { get; set; }
}
