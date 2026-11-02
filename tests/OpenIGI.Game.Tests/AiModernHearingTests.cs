using OpenIGI.Core.Numerics;
using OpenIGI.Game.Igi1.Ai;
using OpenIGI.TestSupport;
using Xunit;

namespace OpenIGI.Game.Tests;

/// <summary>The modern AI's hearing: a heard step turns a soldier to look, a walk carries half as far, ally fire is heard.</summary>
[Collection(ModernAiTests.Name)]
public sealed class AiModernHearingTests : AiSoldierCombatWiringFixture
{
    [RequiresGameDataFact]
    public void AHeardStepTurnsASoldierToLookAndRaisesHisSuspicion()
    {
        using ModernAi _ = new(true);
        Igi1HumanAi soldier = Soldier(out Vector3d position);
        Vector3d player = Behind(soldier, position, 6 * Metre);
        Vector3d eye = player + new Vector3d(0, 0, 1.4 * Metre);
        int tick = Settle(soldier, player);
        Assert.False(soldier.ProbeDetection(eye).InCone, "the player should start out of view");

        Assert.True(soldier.ReceiveWalk(player, player, 20 * Metre));
        for (int end = tick + 120; tick < end; tick++)
        {
            soldier.Tick(tick, player, rootMotionThisTick: 0.0);
            Spend(soldier);
        }

        Assert.True(soldier.ProbeDetection(eye).InCone, "the soldier never turned to the step");
        Assert.True(soldier.PlayerSuspicion > 0.0, "the step raised no suspicion");
    }

    [RequiresGameDataFact]
    public void AWalkCarriesHalfAsFarAsTheStepsFullRadius()
    {
        using ModernAi _ = new(true);
        Igi1HumanAi soldier = Soldier(out Vector3d position);
        Vector3d player = Behind(soldier, position, 8 * Metre);
        Settle(soldier, player);

        Assert.False(soldier.ReceiveWalk(player, player, 12 * Metre), "a walk at 8 m was heard through a 12 m step's half");
        Assert.True(soldier.ReceiveWalk(player, player, 20 * Metre), "a walk at 8 m went unheard through a 20 m step's half");
    }

    [RequiresGameDataFact]
    public void AWallBetweenMufflesAStepToAQuarterOfItsReach()
    {
        using ModernAi _ = new(true);
        Igi1HumanAi soldier = Soldier(out Vector3d position);
        Vector3d player = Behind(soldier, position, 8 * Metre);
        Settle(soldier, player);
        soldier.SetLineOfSight((_, _, _) => false);

        Assert.False(soldier.ReceiveWalk(player, player, 20 * Metre), "a walk at 8 m was heard through a wall that muffles it to 2.5 m");
        Assert.True(soldier.ReceiveWalk(player, player, 70 * Metre), "a walk at 8 m went unheard through a wall that muffles it to 8.75 m");
    }

    [RequiresGameDataFact]
    public void ADoorBetweenMufflesAStepLessThanAWall()
    {
        using ModernAi _ = new(true);
        Igi1HumanAi soldier = Soldier(out Vector3d position);
        Vector3d player = Behind(soldier, position, 8 * Metre);
        Settle(soldier, player);
        soldier.SetHearingCover((_, _) => NoiseCover.Door);

        // A walk behind a door: half the step's radius, then seven tenths of that.
        Assert.False(soldier.ReceiveWalk(player, player, 20 * Metre), "a walk at 8 m was heard through a door that muffles it to 7 m");
        Assert.True(soldier.ReceiveWalk(player, player, 24 * Metre), "a walk at 8 m went unheard through a door that muffles it to 8.4 m");
    }

    [RequiresGameDataFact]
    public void AnAllysGunfireIsHeardWithinItsRangeAndNotBeyond()
    {
        using ModernAi _ = new(true);
        Igi1HumanAi soldier = Soldier(out Vector3d position);
        Igi1HumanAi ally = new(graph: null, position + new Vector3d(10 * Metre, 0, 0), facing: 0f, phase: 1, seed: 2);
        Settle(soldier, Behind(soldier, position, 20 * Metre));

        Assert.False(soldier.HearAllyFire(ally.Position, 5 * Metre, ally));
        Assert.True(soldier.HearAllyFire(ally.Position, 30 * Metre, ally));
    }

    // A point the given distance away that the soldier's cones do not reach, trying the four sides.
    private static Vector3d Behind(Igi1HumanAi soldier, Vector3d position, double distance)
    {
        foreach ((double x, double y) in new[] { (0.0, -1.0), (0.0, 1.0), (-1.0, 0.0), (1.0, 0.0) })
        {
            Vector3d at = position + new Vector3d(x * distance, y * distance, 0);
            if (!soldier.ProbeDetection(at + new Vector3d(0, 0, 1.4 * Metre)).InCone)
            {
                return at;
            }
        }

        throw new InvalidOperationException("the soldier sees all round");
    }

    // A few ticks with the player on offer, so the soldier has registered him.
    private int Settle(Igi1HumanAi soldier, Vector3d player)
    {
        int tick = 1;
        for (; tick <= 8; tick++)
        {
            soldier.Tick(tick, player, rootMotionThisTick: 0.0);
            Spend(soldier);
        }

        return tick;
    }

    private sealed class ModernAi : IDisposable
    {
        private readonly bool _previous = GameplayCVars.ModernAi.Value;

        public ModernAi(bool enabled) => GameplayCVars.ModernAi.Value = enabled;

        public void Dispose() => GameplayCVars.ModernAi.Value = _previous;
    }
}
