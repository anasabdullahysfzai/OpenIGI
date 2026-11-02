using OpenIGI.Core.Numerics;
using OpenIGI.Game.Igi1.Ai;
using OpenIGI.TestSupport;
using Xunit;

namespace OpenIGI.Game.Tests;

/// <summary>The modern AI's suspicion: how it fills and drains, and that a soldier sees the player only once it is full.</summary>
[Collection(ModernAiTests.Name)]
public sealed class AiSuspicionTests : AiSoldierCombatWiringFixture
{
    private const double Scan = 4.0 / 30.0;

    [Fact]
    public void PlainViewFillsInAboutHalfASecond()
    {
        double suspicion = 0.0;
        int scans = 0;
        while (suspicion < 1.0)
        {
            suspicion = AiSuspicion.Step(suspicion, sees: true, viewStrength: 1.0, crouching: false, metresPerSecond: 1.5, alerted: false, Scan);
            scans++;
        }

        Assert.InRange(scans * Scan, 0.4, 0.7);
    }

    [Fact]
    public void FarCrouchedAndStillFillsFarSlowerThanNearStandingAndWalking()
    {
        double near = AiSuspicion.Step(0.0, true, viewStrength: 0.9, crouching: false, metresPerSecond: 1.5, alerted: false, 1.0);
        double far = AiSuspicion.Step(0.0, true, viewStrength: 0.05, crouching: true, metresPerSecond: 0.0, alerted: false, 1.0);

        Assert.True(far * 10 < near, $"far {far} should be under a tenth of near {near}");
    }

    [Fact]
    public void AnAlertedSoldierFillsFaster()
    {
        double calm = AiSuspicion.Step(0.0, true, 0.5, false, 1.5, alerted: false, Scan);
        double alerted = AiSuspicion.Step(0.0, true, 0.5, false, 1.5, alerted: true, Scan);

        Assert.Equal(calm * AiSuspicion.AlertedFactor, alerted, precision: 9);
    }

    /// <summary>A soldier a footstep alerted, at point blank, facing a running player: still a visible moment, not one scan.</summary>
    [Fact]
    public void EvenTheWorstCaseLeavesTheFastestMoment()
    {
        double suspicion = 0.0;
        int scans = 0;
        while (suspicion < 1.0)
        {
            suspicion = AiSuspicion.Step(suspicion, sees: true, viewStrength: 1.0, crouching: false, metresPerSecond: 5.0, alerted: true, Scan);
            scans++;
        }

        Assert.True(scans * Scan >= AiSuspicion.FastestSeconds, $"sure after {scans * Scan:0.00} s");
    }

    [Fact]
    public void OutOfSightItDrainsToNoughtAndNoFurther()
    {
        Assert.Equal(1.0 - AiSuspicion.DrainPerSecond, AiSuspicion.Step(1.0, sees: false, 1.0, false, 0.0, false, 1.0), precision: 9);
        Assert.Equal(0.0, AiSuspicion.Step(0.1, sees: false, 1.0, false, 0.0, false, 10.0));
        Assert.Equal(1.0, AiSuspicion.Step(0.9, sees: true, 1.0, false, 5.0, true, 10.0));
    }

    /// <summary>The gate on a real soldier: the original sees the player the sweep he steps into view; the modern AI later.</summary>
    [RequiresGameDataFact]
    public void WithTheModernAiASoldierSeesThePlayerLaterThanTheOriginalDoes()
    {
        int original = FirstSighting(modern: false);
        int modern = FirstSighting(modern: true);

        Assert.True(original > 0, "the original soldier never saw the player");
        Assert.True(modern > original, $"the modern soldier saw the player on tick {modern}, the original on {original}");
    }

    [RequiresGameDataFact]
    public void AMakingOutSoldierReportsSuspicionBeforeItSees()
    {
        using ModernAiScope _ = new(true);
        Igi1HumanAi soldier = Soldier(out Vector3d position);
        Vector3d player = new(position.X, position.Y + (5 * Metre), position.Z + (5 * Metre / 4));

        double before = 0.0;
        for (int tick = 1; tick <= 300 && !soldier.SeesEnemy; tick++)
        {
            soldier.Tick(tick, player, rootMotionThisTick: 0.0);
            before = Math.Max(before, soldier.SeesEnemy ? 0.0 : soldier.PlayerSuspicion);
            Spend(soldier);
        }

        Assert.True(soldier.SeesEnemy, "the modern soldier never saw the player");
        Assert.InRange(before, double.Epsilon, 0.999999);
    }

    private int FirstSighting(bool modern)
    {
        using ModernAiScope _ = new(modern);
        Igi1HumanAi soldier = Soldier(out Vector3d position);
        Vector3d player = new(position.X, position.Y + (5 * Metre), position.Z + (5 * Metre / 4));
        for (int tick = 1; tick <= 300; tick++)
        {
            soldier.Tick(tick, player, rootMotionThisTick: 0.0);
            if (soldier.SeesEnemy)
            {
                return tick;
            }

            Spend(soldier);
        }

        return -1;
    }

    private sealed class ModernAiScope : IDisposable
    {
        private readonly bool _previous = GameplayCVars.ModernAi.Value;

        public ModernAiScope(bool enabled) => GameplayCVars.ModernAi.Value = enabled;

        public void Dispose() => GameplayCVars.ModernAi.Value = _previous;
    }
}

/// <summary>Tests that turn the modern AI on, which every soldier reads: run alone, so no other test sees it on.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ModernAiTests
{
    /// <summary>The collection's name.</summary>
    public const string Name = "Modern AI";
}
