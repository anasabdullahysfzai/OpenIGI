using OpenIGI.Core.Numerics;
using OpenIGI.Game.Ai;

namespace OpenIGI.Game.Igi1.Ai;

/// <content>
/// The modern AI's hearing (<see cref="GameplayCVars.ModernAi"/>): a soldier who hears the player investigates instead of
/// going straight to a fight, a walk carries half as far as a run, a door between muffles it a little and a wall to a quarter,
/// and the gunfire of a soldier's own side brings him in.
/// <b>Not retail.</b>
/// </content>
public sealed partial class Igi1HumanAi
{
    /// <summary>The share of a footstep's retail radius a walk carries; a run carries all of it, a crouch nothing (retail).</summary>
    public const double WalkingHearingShare = 0.5;

    /// <summary>The share of its range a noise carries with a wall, a floor or a roof between it and the soldier.</summary>
    public const double MuffledHearingShare = 0.25;

    /// <summary>The share of its range a noise carries with only a door between it and the soldier.</summary>
    public const double DoorHearingShare = 0.7;

    /// <summary>How far above a step the sound is listened for, in world units, so the ground it is on does not muffle it.</summary>
    private const double HeardAboveGroundInUnits = 0.5 * 4096.0;

    /// <summary>How much suspicion of the player one heard step adds, walking.</summary>
    public const double HeardStepSuspicion = 0.12;

    /// <summary>How much suspicion of the player one heard step adds, running.</summary>
    public const double HeardRunSuspicion = 0.25;

    /// <summary>The most suspicion hearing alone can give: the soldier still has to see the player to be sure.</summary>
    public const double HeardSuspicionCap = 0.5;

    /// <summary>Above this suspicion a soldier walks over to where he heard the player, not only turns to look.</summary>
    public const double WalkOverSuspicion = 0.3;

    /// <summary>How long a soldier stands looking at where he heard something, in seconds.</summary>
    public const float LookSeconds = 2.5f;

    // Where the soldier heard something since its last tick, which it investigates on its next.
    private Vector3d? _heardAt;

    // What stands between him and a noise (SetHearingCover); without it, the sight test's wall or nothing.
    private Func<Vector3d, Vector3d, NoiseCover>? _hearingCover;

    /// <summary>Gives the soldier the modern AI's test of what stands between him and a noise: nothing, a door or a wall.</summary>
    /// <param name="cover">The test, or <see langword="null"/> to fall back on the sight test.</param>
    public void SetHearingCover(Func<Vector3d, Vector3d, NoiseCover>? cover) => _hearingCover = cover;

    /// <summary>
    /// The modern AI's answer to a step, a landing or a round striking near him: the walk is heard at half the range, and what
    /// he hears raises his suspicion of the player and sends him to look rather than into a fight. Retail posts the event,
    /// and the archetypes answer WALK and GROUNDIMPACT with SetCombat.
    /// </summary>
    /// <returns><see langword="true"/> when he heard it.</returns>
    private bool HearNoiseModern(Igi1AiTrackedHuman player, Vector3d position, double distance, double radiusInUnits)
    {
        bool running = player.MetresPerSecond > AiSuspicion.RunningAboveMetresPerSecond;
        double reach = radiusInUnits * (running ? 1.0 : WalkingHearingShare);

        // A door between keeps most of the step; a wall, a floor or a roof a quarter. From his eye to just above the step.
        Vector3d above = new(position.X, position.Y, position.Z + HeardAboveGroundInUnits);
        NoiseCover cover = _hearingCover?.Invoke(EyePosition(), above)
            ?? (_sight is not null && !_sight(EyePosition(), above, AiDetectionCandidate.PlayerId) ? NoiseCover.Wall : NoiseCover.Open);
        reach *= cover switch
        {
            NoiseCover.Door => DoorHearingShare,
            NoiseCover.Wall => MuffledHearingShare,
            _ => 1.0,
        };

        if (distance > reach)
        {
            return false;
        }

        double gain = running ? HeardRunSuspicion : HeardStepSuspicion;
        player.Suspicion = Math.Max(player.Suspicion, Math.Min(HeardSuspicionCap, player.Suspicion + gain));
        _heardAt = position;
        return true;
    }

    /// <summary>
    /// The modern AI's answer to his own side's gunfire within its range: the shooter's fight is his, as a squad alert from
    /// the shooter would make it; a soldier whose script will not take the alert goes to look instead. Retail drops a
    /// shot from his own side unheard.
    /// </summary>
    /// <param name="origin">Where the shot was fired.</param>
    /// <param name="range">How far the shot carries.</param>
    /// <param name="shooter">The soldier who fired.</param>
    /// <returns><see langword="true"/> when he heard it.</returns>
    public bool HearAllyFire(Vector3d origin, double range, Igi1HumanAi shooter)
    {
        ArgumentNullException.ThrowIfNull(shooter);
        if (Activity == AiSoldierActivity.Dead || _combatInstalled || Vector3d.Distance(Position, origin) > range)
        {
            return false;
        }

        if (!ReceiveAlert(origin, shooter.RuntimeId, shooter.TargetId, range))
        {
            _heardAt = origin;
        }

        return true;
    }

    /// <summary>Turns him to what he heard, and walks him over to it when he is suspicious enough; once a tick, before the script.</summary>
    /// <remarks>
    /// Installed as the script installs its own actions, so whatever the script answers this tick -- the sight of the player,
    /// a round -- displaces it, and a soldier already looking or walking to a noise keeps going.
    /// </remarks>
    private void InvestigateWhatWasHeard()
    {
        if (_heardAt is not { } heard)
        {
            return;
        }

        _heardAt = null;
        if (_combatInstalled || _lookAtPoint is not null || IsActionActive("AIAction_MoveToEvent"))
        {
            return;
        }

        bool walkOver = PlayerSuspicion >= WalkOverSuspicion;
        AiProducedAction look = new("AIAction_LookAtEvent", [0, (int)AiActionFlagWord.None], 0, [LookSeconds, 0f]);
        AiProducedAction move = new("AIAction_MoveToEvent", [0, (int)AiActionFlagWord.None], 0);

        Vector3d? before = _currentEventPosition;
        _currentEventPosition = heard;
        ApplyActions(walkOver ? [move, look] : [look]);
        _currentEventPosition = before;
    }
}
