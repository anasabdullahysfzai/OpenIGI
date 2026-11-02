using OpenIGI.Core.Numerics;
using OpenIGI.Game.Igi1.Ai;
using OpenIGI.TestSupport;
using Xunit;

namespace OpenIGI.Game.Tests;

/// <summary>The modern AI's glimpse: a soldier making the player out walks towards him, and one who loses him goes where he last saw him.</summary>
[Collection(ModernAiTests.Name)]
public sealed class AiInvestigationTests : AiSoldierCombatWiringFixture
{
    [RequiresGameDataFact]
    public void ASoldierMakingThePlayerOutWalksTowardsHim()
    {
        using ModernAi _ = new(true);
        Igi1HumanAi soldier = Soldier(out Vector3d position);
        Vector3d player = FarInView(soldier, position);

        int tick = 1;
        for (; tick <= 600 && !soldier.SeesEnemy && !WalkingOver(soldier); tick++)
        {
            soldier.Tick(tick, player, rootMotionThisTick: 0.0);
            Spend(soldier);
        }

        Assert.NotNull(soldier.InvestigatingAt);
        Assert.Contains(soldier.LastActions, action => action.Name == "AIAction_MoveToEvent");
        Assert.True(Vector3d.Distance(soldier.InvestigatingAt!.Value, player) < Metre, "he walked somewhere other than the player");
    }

    [RequiresGameDataFact]
    public void ASoldierWhoLosesThePlayerGoesWhereHeLastGlimpsedHim()
    {
        using ModernAi _ = new(true);
        Igi1HumanAi soldier = Soldier(out Vector3d position);
        Vector3d seen = FarInView(soldier, position);

        int tick = 1;
        for (; tick <= 600 && !WalkingOver(soldier); tick++)
        {
            soldier.Tick(tick, seen, rootMotionThisTick: 0.0);
            Spend(soldier);
        }

        Assert.NotNull(soldier.InvestigatingAt);

        // The player slips out of view, and the soldier is told nothing of where he went.
        soldier.SetLineOfSight((_, _, _) => false);
        Vector3d gone = seen + new Vector3d(15 * Metre, 15 * Metre, 0);
        for (int end = tick + 12; tick < end; tick++)
        {
            soldier.Tick(tick, gone, rootMotionThisTick: 0.0);
            Spend(soldier);
        }

        Assert.False(soldier.SeesEnemy);
        Assert.True(Vector3d.Distance(soldier.InvestigatingAt!.Value, seen) < 2 * Metre, "he went where the player went, not where he saw him");

        // Nearly sure, he jogs there; less, he walks.
        Assert.Equal(soldier.PlayerSuspicion < Igi1HumanAi.JogSuspicion, soldier.IsWalking);

        // Still on his way, and still sure enough to be: an investigating soldier does not forget.
        Assert.True(soldier.IsInvestigating, "he gave up before getting there");
        Assert.True(soldier.PlayerSuspicion >= Igi1HumanAi.WalkOverSuspicion, $"his suspicion drained to {soldier.PlayerSuspicion:0.00} while investigating");
    }

    [RequiresGameDataFact]
    public void FindingNothingHeSearchesLookingAllRoundThenStaysOnEdge()
    {
        using ModernAi _ = new(true);
        Igi1HumanAi soldier = Soldier(out Vector3d position);
        int tick = LoseThePlayer(soldier, position);

        bool searched = false;
        float? start = null;
        double widest = 0.0;
        for (int end = tick + 3000; tick < end && !(searched && !soldier.IsSearching); tick++)
        {
            // The way he faced as the search was planned: its first tick already turns him.
            float before = soldier.Facing;
            soldier.Tick(tick, position + new Vector3d(40 * Metre, 40 * Metre, 0), rootMotionThisTick: 0.0);
            Spend(soldier);
            if (soldier.IsSearching)
            {
                searched = true;
                start ??= before;
                widest = Math.Max(widest, Math.Abs(Math.IEEERemainder(soldier.Facing - start.Value, 2 * Math.PI)));
            }
        }

        Assert.True(searched, "he never searched");
        Assert.True(widest > 2.5, $"he turned only {widest:0.00} rad while searching, never behind him");
        Assert.False(soldier.IsSearching, "the search never ended");
        Assert.True(soldier.IsOnEdge, "he was not on edge after the search");
    }

    [RequiresGameDataFact]
    public void GlimpsingThePlayerAgainEndsTheSearchForAnInvestigation()
    {
        using ModernAi _ = new(true);
        Igi1HumanAi soldier = Soldier(out Vector3d position);
        int tick = LoseThePlayer(soldier, position);
        Vector3d away = position + new Vector3d(40 * Metre, 40 * Metre, 0);
        for (int end = tick + 3000; tick < end && !soldier.IsSearching; tick++)
        {
            soldier.Tick(tick, away, rootMotionThisTick: 0.0);
            Spend(soldier);
        }

        Assert.True(soldier.IsSearching, "he never searched");

        // He can see again, and the player stands where he is looking: within a second the search is dropped for him.
        soldier.SetLineOfSight(null);
        Vector3d shown = FarInView(soldier, soldier.Position);
        for (int end = tick + 30; tick < end && soldier.IsSearching; tick++)
        {
            soldier.Tick(tick, shown, rootMotionThisTick: 0.0);
            Spend(soldier);
        }

        Assert.False(soldier.IsSearching, "the search went on for a second with the player in view");
        Assert.True(soldier.SeesEnemy || Vector3d.Distance(soldier.InvestigatingAt!.Value, shown) < 2 * Metre, "he neither saw the player nor went to him");
    }

    // Has the soldier glimpse the player, start towards him and lose him -- blinded, the player gone -- and returns the tick.
    private int LoseThePlayer(Igi1HumanAi soldier, Vector3d position)
    {
        Vector3d seen = FarInView(soldier, position);
        int tick = 1;
        for (; tick <= 600 && !WalkingOver(soldier); tick++)
        {
            soldier.Tick(tick, seen, rootMotionThisTick: 0.0);
            Spend(soldier);
        }

        Assert.NotNull(soldier.InvestigatingAt);
        soldier.SetLineOfSight((_, _, _) => false);
        return tick;
    }

    // Whether he has set off towards something: a first glimpse only turns him to look, the walk comes as suspicion rises.
    private static bool WalkingOver(Igi1HumanAi soldier) =>
        soldier.LastActions.Any(action => action.Name == "AIAction_MoveToEvent");

    // The farthest point straight in view, where suspicion fills slowest, so the approach starts before he is sure.
    private static Vector3d FarInView(Igi1HumanAi soldier, Vector3d position)
    {
        Vector3d? best = null;
        foreach ((double x, double y) in new[] { (0.0, -1.0), (0.0, 1.0), (-1.0, 0.0), (1.0, 0.0) })
        {
            for (int metres = 4; metres <= 60; metres += 2)
            {
                Vector3d at = position + new Vector3d(x * metres * Metre, y * metres * Metre, 0);
                if (soldier.ProbeDetection(at + new Vector3d(0, 0, 1.4 * Metre)).InCone
                    && (best is null || Vector3d.Distance(position, at) > Vector3d.Distance(position, best.Value)))
                {
                    best = at;
                }
            }
        }

        return best ?? throw new InvalidOperationException("nothing is in the soldier's view");
    }

    private sealed class ModernAi : IDisposable
    {
        private readonly bool _previous = GameplayCVars.ModernAi.Value;

        public ModernAi(bool enabled) => GameplayCVars.ModernAi.Value = enabled;

        public void Dispose() => GameplayCVars.ModernAi.Value = _previous;
    }
}
