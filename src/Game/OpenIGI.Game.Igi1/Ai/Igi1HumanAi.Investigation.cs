using OpenIGI.Core.Numerics;

namespace OpenIGI.Game.Igi1.Ai;

/// <content>
/// The modern AI's answer to a glimpse (<see cref="GameplayCVars.ModernAi"/>): a soldier making the player out walks
/// towards him while his suspicion fills, and one who loses sight of him before he is sure goes to the place he last
/// glimpsed him -- not to where the player has gone since. <b>Not retail</b>, whose soldier has no stage short of seeing.
/// </content>
public sealed partial class Igi1HumanAi
{
    /// <summary>How far from what he is going to look at the player has to be seen for the soldier to turn to him instead.</summary>
    private const double RetargetInUnits = 2.0 * 4096.0;

    /// <summary>Gets or sets where the modern AI says what this soldier decides, for reading a play-test back.</summary>
    public Action<string>? ModernLog { get; set; }

    /// <summary>Gets the rank an event has to be above to reach the script now, for testing: the head action's.</summary>
    public int PriorityCapForTesting => _pendingActions.PriorityCap;

    /// <summary>Gets the action at the head of the soldier's stack, the one running this tick, without its prefix.</summary>
    public string? RunningAction => _runningAction?.Replace("AIAction_", string.Empty, StringComparison.Ordinal);

    /// <summary>Follows up what the scan's gate just made of the player; the investigation itself runs on the next tick.</summary>
    /// <param name="player">The player as this soldier tracks him, the gate's verdict and suspicion already updated.</param>
    /// <param name="sawBefore">Whether the sight test passed on the scan before this one.</param>
    /// <param name="rayRan">
    /// Whether the sight ray was cast this scan. Between casts the sweep keeps its last verdict, and taking the player's
    /// place then would follow him round a corner he had already slipped behind.
    /// </param>
    private void FollowGlimpse(Igi1AiTrackedHuman player, bool sawBefore, bool rayRan)
    {
        if (player.SeesRaw && rayRan)
        {
            player.GlimpsedFeet = player.Candidate.Feet;
        }

        // Every change in what his cone and ray make of the player, sure or not, for reading a play-test back.
        if (player.SeesRaw != player.SeenAtLastRay)
        {
            player.SeenAtLastRay = player.SeesRaw;
            ModernLog?.Invoke(FormattableString.Invariant(
                $"{(player.SeesRaw ? "sees" : "lost sight of")} the player, suspicion {player.Suspicion:0.00}, {player.MetresPerSecond:0.0} m/s, {(_combatInstalled ? "fighting" : _searching ? "searching" : IsInvestigating ? "investigating" : "idle")}"));
        }

        if (player.Suspicion <= 0.0)
        {
            player.Approached = false;
            return;
        }

        // Sure is the original's fight.
        if (_combatInstalled || player.Suspicion >= 1.0 || player.GlimpsedFeet is not { } glimpsed)
        {
            return;
        }

        // Seen -- the ray confirming him -- while searching, or while going to look somewhere else: whatever he was doing,
        // the player is what he turns to now, however unsure; a search sweep would only turn him away again.
        bool seenNow = player.SeesRaw && rayRan;

        // Seen while doing nothing of his own -- a look at him just ended -- is looked at again, so he keeps his eyes on the
        // player until he is sure or loses him.
        bool seenWhileBusyElsewhere = seenNow
            && (_searching
                || !IsInvestigating
                || (InvestigatingAt is { } at && Vector3d.Distance(at, glimpsed) > RetargetInUnits));

        // Otherwise, not suspicious enough is still only a look.
        if (player.Suspicion < WalkOverSuspicion && !seenWhileBusyElsewhere)
        {
            return;
        }

        bool startsToApproach = player.SeesRaw && !player.Approached;
        bool lostHim = sawBefore && !player.SeesRaw;
        if (startsToApproach || lostHim || seenWhileBusyElsewhere)
        {
            // Only a walk over counts as setting off towards him: a look at a first glimpse leaves the walk still to come.
            player.Approached = player.Suspicion >= WalkOverSuspicion;
            _heardAt = glimpsed;
            _redirect = true;
        }
    }
}
