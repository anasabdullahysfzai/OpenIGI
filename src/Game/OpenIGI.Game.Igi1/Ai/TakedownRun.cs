using OpenIGI.Core.Graphics;
using OpenIGI.Core.Numerics;
using OpenIGI.Game.Igi1.Player;

namespace OpenIGI.Game.Igi1.Ai;

/// <summary>
/// One knife takedown under way: the victim stops thinking and the player stops playing while a paired clip runs on a modern body each.
/// The presentation poses both and hands back the eye and where the player ends up. <b>Not retail.</b>
/// </summary>
/// <param name="victim">The soldier taken down.</param>
/// <param name="startEye">The player's eye as it began, which the view leaves from.</param>
public sealed class TakedownRun(SoldierView victim, Camera3D startEye)
{
    /// <summary>How far the player looks down as it ends, at the man at his feet, in radians.</summary>
    public const float EndPitch = -0.45f;

    /// <summary>How long the pair runs, in ticks.</summary>
    public const int Ticks = 97;

    /// <summary>Gets the soldier taken down.</summary>
    public SoldierView Victim { get; } = victim;

    /// <summary>Gets the player's eye as it began.</summary>
    public Camera3D StartEye { get; } = startEye;

    /// <summary>Gets the soldiers who have seen it and been told.</summary>
    public HashSet<SoldierView> Witnesses { get; } = [];

    /// <summary>Gets or sets how many ticks it has run.</summary>
    public int Tick { get; set; }

    /// <summary>Gets or sets the eye the frame is drawn from, by how far the frame is between ticks; null until the bodies are posed.</summary>
    public Func<float, Camera3D>? Eye { get; set; }

    /// <summary>Gets or sets where the player's feet end up, in world units, and the facing they end up with; null until the bodies are posed.</summary>
    public (Vector3d Feet, float Gamma)? End { get; set; }
}
