using OpenIGI.Core.Numerics;
using OpenIGI.Game;
using OpenIGI.Game.Ai;
using OpenIGI.Game.Igi1.Ai;
using OpenIGI.TestSupport;
using Xunit;

namespace OpenIGI.Desktop.Tests;

/// <summary>
/// The modern AI's answer to fire on mission 1's own patrol guards, with the level's graphs and walls: a guard fighting the
/// player who is shot runs to a place the player's eye cannot see his chest from, stays down a moment, and fights on.
/// </summary>
[Collection(ModernAiMissionTests.Name)]
public sealed class ModernCoverMissionTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const double Metre = 4096.0;

    [RequiresGameDataFact]
    public void APatrolGuardShotAtRunsForCoverAndFightsOnFromIt()
    {
        bool previous = GameplayCVars.ModernAi.Value;
        GameplayCVars.ModernAi.Value = true;
        try
        {
            using SoldierRendererScenario scenario = SoldierRendererScenario.CreateScenario(prepareContainers: true, missionNumber: 1, combatRows: true);
            for (int tick = 0; tick < 10; tick++)
            {
                scenario.Renderer.Tick();
            }

            List<string> tried = [];
            IReadOnlyList<Igi1HumanAi> brains = scenario.Renderer.Simulation.BrainsForTesting;
            tried.Add($"{brains.Count} brains, {brains.Count(b => b.PlayerIsEnemy)} hostile, {brains.Count(b => b.HasGraph)} with a graph");
            foreach (Igi1HumanAi guard in scenario.Renderer.Simulation.BrainsForTesting.Where(b => b.PlayerIsEnemy && b.HasGraph))
            {
                if (Attempt(guard, tried) is { } story)
                {
                    output.WriteLine(story);
                    return;
                }
            }

            Assert.Fail("no patrol guard took cover:" + Environment.NewLine + string.Join(Environment.NewLine, tried));
        }
        finally
        {
            GameplayCVars.ModernAi.Value = previous;
        }
    }

    // The player ten metres ahead of the guard where the guard can see him; fight, a round in him, and what he does.
    private static string? Attempt(Igi1HumanAi guard, List<string> tried)
    {
        Matrix3f eye = guard.CurrentEyeOrientation;
        double length = Math.Sqrt((eye.M12 * eye.M12) + (eye.M22 * eye.M22));
        if (length < 0.5)
        {
            return null;
        }

        Vector3d feet = new(guard.Position.X + (eye.M12 / length * 10.0 * Metre), guard.Position.Y + (eye.M22 / length * 10.0 * Metre), guard.Position.Z);
        Vector3d playerEye = feet + new Vector3d(0, 0, 1.6 * Metre);
        if (!guard.CanSeeForTesting(guard.Position + new Vector3d(0, 0, 1.6 * Metre), playerEye))
        {
            tried.Add($"guard at {guard.Position}: cannot see ten metres ahead");
            return null;
        }

        List<string> story = [];
        guard.ModernLog = text => story.Add(text);
        int tick = 1, engaged = -1;
        for (; tick <= 900 && engaged < 0; tick++)
        {
            guard.Tick(tick, playerEye, rootMotionThisTick: 0.0, enemyFeet: feet);
            if (guard.Activity == AiSoldierActivity.Engaging)
            {
                engaged = tick;
            }
        }

        if (engaged < 0 || guard.Arm != AiCombatArm.Patrol)
        {
            tried.Add($"guard at {guard.Position}: engaged {engaged}, arm {guard.Arm}");
            return null;
        }

        // Shot, and walked by the clip's speed: a run is about a ninth of a metre a tick.
        Vector3d from = guard.Position;
        guard.ReceiveDamage(feet);
        string? phase = null;
        Dictionary<string, int> ticksIn = [];
        int positions = 0;
        for (int end = tick + 600; tick < end; tick++)
        {
            double stride = guard.CoverState is "Moving" or "Advancing" && guard.RouteForTesting.Forward ? 0.11 * Metre : 0.0;
            guard.Tick(tick, playerEye, rootMotionThisTick: stride, enemyFeet: feet);
            guard.StepBodyAfterClip(stride, 0.0);
            ticksIn[guard.CoverState] = ticksIn.GetValueOrDefault(guard.CoverState) + 1;
            if (guard.CoverState != phase)
            {
                phase = guard.CoverState;
                story.Add($"t{tick} {phase} node {guard.CoverNode}");
                positions += phase == "Moving" ? 1 : 0;
            }
        }

        string told = $"guard from {from}: " + string.Join(" | ", story);
        if (positions == 0)
        {
            tried.Add(told);
            return null;
        }

        // He went somewhere to fight from, and fought from it.
        double moved = Vector3d.Distance(from, guard.Position) / Metre;
        Assert.True(ticksIn.GetValueOrDefault("Firing") > 2 * 30, $"he hardly fired. {told}");
        Assert.True(moved > 0.5, $"he moved only {moved:0.00} m. {told}");
        return $"{told} | moved {moved:0.0} m; " + string.Join(", ", ticksIn.Select(t => $"{t.Key} {t.Value / 30.0:0.0}s"));
    }
}
