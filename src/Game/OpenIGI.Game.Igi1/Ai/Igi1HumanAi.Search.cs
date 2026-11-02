using OpenIGI.Core.Numerics;
using OpenIGI.Formats.Ai;

namespace OpenIGI.Game.Igi1.Ai;

/// <content>
/// The modern AI's search (<see cref="GameplayCVars.ModernAi"/>): a soldier who walked over to something he heard or glimpsed,
/// and found nothing there, searches the place as a modern stealth game's soldier does. He looks left, right and behind him;
/// he walks to a couple of the graph's nodes nearby and looks about at each; then he goes back to his patrol, on edge for a
/// while. Anything he sees or hears on the way ends the search. <b>Not retail.</b>
/// </content>
public sealed partial class Igi1HumanAi
{
    /// <summary>How far to either side the soldier turns to look, in radians.</summary>
    public const double SweepRadians = 70.0 * Math.PI / 180.0;

    /// <summary>How long he holds each look of a search once turned, in seconds.</summary>
    public const float SweepSeconds = 1.0f;

    /// <summary>How far from the searched place a node may be for him to go and look from it, in world units.</summary>
    public const double SearchRadiusInUnits = 12.0 * 4096.0;

    /// <summary>How many nodes about the place he goes and looks from.</summary>
    public const int SearchNodes = 2;

    /// <summary>How long after a search he stays on edge, in ticks: suspicion fills twice as fast.</summary>
    public const int OnEdgeTicks = 45 * 30;

    // How far ahead of himself he looks, in world units: far enough to be level.
    private const double LookAheadInUnits = 5.0 * 4096.0;

    // The search still to run, in order: a point to look at or walk to, and, for a walk, what to look at on arriving.
    private readonly Queue<(Vector3d Point, bool Walk, Vector3d? LookAt)> _searchSteps = new();

    private bool _searching;

    // Whether the last investigation walked over to its point; a search follows it once it ends.
    private bool _investigationWalked;

    private int _onEdgeUntilTick = -1;

    /// <summary>Gets whether the soldier is searching a place for the player.</summary>
    public bool IsSearching => _searching;

    /// <summary>Gets whether the soldier is searching, or has lately searched: his suspicion fills twice as fast.</summary>
    public bool IsOnEdge => _searching || _lastTick < _onEdgeUntilTick;

    /// <summary>
    /// Starts a search once a walk-over investigation has ended with nothing found, and installs its next step once the last
    /// has run; once a tick, before the script, so a step between two others is not taken for idleness.
    /// </summary>
    private void ContinueSearch()
    {
        if (_combatInstalled || Activity == AiSoldierActivity.Dead)
        {
            CancelSearch();
            return;
        }

        // A look or a walk -- of the investigation, or of the search -- is still running.
        if (_lookAtPoint is not null || IsActionActive("AIAction_MoveToEvent"))
        {
            return;
        }

        if (!_searching)
        {
            // Nobody searches for a dead man: the walk that would have led to it is forgotten.
            _tracked.TryGetValue(AiDetectionCandidate.PlayerId, out Igi1AiTrackedHuman? player);
            if (player is { Candidate.Alive: false })
            {
                _investigationWalked = false;
            }

            // Nor for a man he is looking at: the glimpse keeps his eyes on him (FollowGlimpse).
            if (!_investigationWalked || InvestigatingAt is not { } place || player is { SeesRaw: true })
            {
                return;
            }

            _investigationWalked = false;
            PlanSearch(place);
            ModernLog?.Invoke($"found nothing; searching ({_searchSteps.Count} steps)");
        }

        if (_searchSteps.TryDequeue(out (Vector3d Point, bool Walk, Vector3d? LookAt) step))
        {
            Install(step.Point, step.Walk, SweepSeconds, step.LookAt);
            return;
        }

        _searching = false;
        _onEdgeUntilTick = _lastTick + OnEdgeTicks;
        ModernLog?.Invoke("search over; back to patrol, on edge");
    }

    /// <summary>Ends a search where it stands, without the time on edge a finished one leaves.</summary>
    private void CancelSearch()
    {
        if (_searching)
        {
            ModernLog?.Invoke("search dropped");
        }

        _searchSteps.Clear();
        _searching = false;
    }

    /// <summary>Lays out the search of a place: the looks about where he stands, then the nodes nearby.</summary>
    private void PlanSearch(Vector3d place)
    {
        _searching = true;
        _searchSteps.Clear();
        Vector3d here = Position;
        double facing = Facing;

        // Left, right, then behind -- each a look at a point ahead, at his own height.
        foreach (double turn in new[] { SweepRadians, -SweepRadians, Math.PI })
        {
            Vector3d ahead = Ahead(here, facing + turn);
            _searchSteps.Enqueue((ahead, false, null));
        }

        // A couple of nodes about the place, not the one he stands on, in an order of the level's own dice; at each he looks out
        // away from the place, then to either side of that.
        if (_graph is null || _graph.InParentFrame)
        {
            return;
        }

        int standing = NearestNode(here);
        List<GraphNode> near = [.. _graph.Nodes.Where(node =>
            node.Id != standing && Vector3d.Distance(node.Position, place) <= SearchRadiusInUnits)];
        for (int i = 0; i < SearchNodes && near.Count > 0; i++)
        {
            GraphNode node = near[(int)(_random.NextDouble() * near.Count) % near.Count];
            near.Remove(node);
            double outward = Math.Atan2(-(node.Position.X - place.X), node.Position.Y - place.Y);
            _searchSteps.Enqueue((node.Position, true, Ahead(node.Position, outward)));
            _searchSteps.Enqueue((Ahead(node.Position, outward + (SweepRadians * 0.7)), false, null));
            _searchSteps.Enqueue((Ahead(node.Position, outward - (SweepRadians * 0.7)), false, null));
        }
    }

    // A point ahead of a place along a heading -- the facing's convention, atan2(-dx, dy) -- at the place's own height.
    private static Vector3d Ahead(Vector3d from, double heading) =>
        new(from.X - (Math.Sin(heading) * LookAheadInUnits), from.Y + (Math.Cos(heading) * LookAheadInUnits), from.Z);
}
