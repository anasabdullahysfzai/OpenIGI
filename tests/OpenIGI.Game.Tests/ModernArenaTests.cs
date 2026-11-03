using OpenIGI.Core.Numerics;
using OpenIGI.Game.Ai;
using OpenIGI.Game.Igi1.Ai;
using OpenIGI.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace OpenIGI.Game.Tests;

/// <summary>Fights in the test room (<see cref="ModernArena"/>), each read back tick by tick. <b>Not retail.</b></summary>
[Collection(ModernAiTests.Name)]
public sealed class ModernArenaTests(ITestOutputHelper output)
{
    private const double Metre = ModernArena.Metre;

    /// <summary>
    /// The play-test of 2026-11-03, guard 2017: the player in plain view 13.4 m off, rounds cracking past at 0.4 s and 2.1 s.
    /// The game took 3.2 s from first sight to the fight; the room should take as long if it is the same soldier.
    /// </summary>
    [RequiresGameDataTheory]
    [InlineData(null, 60.0)]
    [InlineData(2217, 60.0)]
    [InlineData(null, 0.0)]
    [InlineData(2217, 0.0)]
    public void ShotPastInPlainViewHeFightsBack(int? missionTask, double heard)
    {
        (int sure, List<string> story) = Run(arena => arena, guard: (15, 5), player: (15, 18.4), shots: [13, 14, 15, 64, 65, 66], ticks: 240, missionTask, heard);
        output.WriteLine($"script {missionTask?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "patrol"}, gunshot heard {heard} m: engaged at {sure / 30.0:0.00}s");
        output.WriteLine(string.Join(Environment.NewLine, story));
        Assert.True(sure > 0, "he never fought back");

        // Rounds cracking past him from a man in plain view: he fights within a second of the first, heard or not.
        Assert.True(sure < 13 + 30, $"he took {sure / 30.0:0.00}s to fight back");
    }

    /// <summary>
    /// The play-test's cover loop: the player strafing at a run while he fires, so cover hidden from where he was is open from
    /// where he is a second later. The guard should not go back to a place he was just seen in, nor run from cover to cover
    /// all fight long.
    /// </summary>
    [RequiresGameDataFact]
    public void AStrafingShooterDoesNotLoopHimThroughCover()
    {
        bool previous = GameplayCVars.ModernAi.Value;
        GameplayCVars.ModernAi.Value = true;
        try
        {
            // Only narrow cover, as in the mission-one fight: two columns, which hide a man from straight ahead but not once
            // the shooter has moved a few metres across, and a waist-high wall in front of him.
            ModernArena arena = new ModernArena()
                .Box(12, 9, 0, 18, 9.6, 1.0)
                .Box(19.5, 9, 0, 20.5, 10, 3.0)
                .Box(9.5, 11, 0, 10.5, 12, 3.0);
            arena.Build();
            List<string> story = [];
            int tick = 0;
            Igi1HumanAi soldier = arena.Patrol(ModernArena.At(15, 6), 0f, text => story.Add($"{tick / 30.0:0.00}s {text}"));

            // From in front of him across to his right and back, at a run, firing a round past him every half second he is in view.
            const double Run = 2.2 / 30.0;
            string was = "None";
            int fired = 0, firing = 0;
            for (tick = 1; tick <= 30 * 30; tick++)
            {
                double leg = (tick * Run) % 24.0;
                double x = 15.0 + (leg < 12.0 ? leg : 24.0 - leg);
                Vector3d feet = ModernArena.At(x, 24);
                Vector3d eye = feet + new Vector3d(0, 0, 1.6 * Metre);
                Vector3d head = soldier.Position + new Vector3d(0, 0, 1.5 * Metre);
                if (tick % 15 == 0 && arena.Clear(eye, head))
                {
                    fired++;
                    arena.Shoot(soldier, eye, soldier.Position + new Vector3d(1.0 * Metre, 0, 1.2 * Metre), heard: 0.0);
                }

                arena.Tick(soldier, tick, feet);
                firing += soldier.CoverState == "Firing" ? 1 : 0;
                if (soldier.CoverState != was)
                {
                    was = soldier.CoverState;
                    story.Add(FormattableString.Invariant($"{tick / 30.0:0.00}s    {was} node {soldier.CoverNode}, player at x={x:0.0}"));
                }
            }

            // He fights from places and moves between them; he does not run about all fight long nor go back where he was seen.
            int relocations = story.Count(line => line.Contains("takes a position", StringComparison.Ordinal) || line.Contains("flanks to", StringComparison.Ordinal));
            output.WriteLine($"{fired} rounds; {relocations} positions taken; firing {firing / 30.0:0.0}s of 30");
            output.WriteLine(string.Join(Environment.NewLine, story));
            Assert.True(relocations <= 6, $"he took {relocations} positions in thirty seconds");
            Assert.True(firing > 6 * 30, $"he fired for only {firing / 30.0:0.0}s");
        }
        finally
        {
            GameplayCVars.ModernAi.Value = previous;
        }
    }

    // A fight: the guard facing the player, the player standing still and firing a round on each tick listed, a metre wide.
    private static (int Engaged, List<string> Story) Run(
        Func<ModernArena, ModernArena> room, (double X, double Y) guard, (double X, double Y) player, int[] shots, int ticks, int? missionTask = null, double heard = 60.0)
    {
        bool previous = GameplayCVars.ModernAi.Value;
        GameplayCVars.ModernAi.Value = true;
        try
        {
            ModernArena arena = room(new ModernArena());
            arena.Build();
            List<string> story = [];
            int tick = 0;
            Igi1HumanAi soldier = arena.Patrol(ModernArena.At(guard.X, guard.Y), 0f, text => story.Add($"{tick / 30.0:0.00}s {text}"), missionTask);

            Vector3d feet = ModernArena.At(player.X, player.Y);
            Vector3d eye = feet + new Vector3d(0, 0, 1.6 * Metre);
            int engaged = -1;
            for (tick = 1; tick <= ticks; tick++)
            {
                if (shots.Contains(tick))
                {
                    // A metre to his side, at his chest.
                    Vector3d aim = soldier.Position + new Vector3d(1.0 * Metre, 0, 1.2 * Metre);
                    story.Add($"{tick / 30.0:0.00}s -- player fires, {(arena.Shoot(soldier, eye, aim, heard) ? "hits" : "misses")}");
                }

                double stride = soldier.WalksForward ? (soldier.Activity == AiSoldierActivity.Engaging ? 0.11 : 0.04) * Metre : 0.0;
                soldier.Tick(tick, eye, rootMotionThisTick: stride, enemyFeet: feet);
                soldier.StepBodyAfterClip(stride, 0.0);
                if (soldier.Activity == AiSoldierActivity.Engaging && engaged < 0)
                {
                    engaged = tick;
                    story.Add($"{tick / 30.0:0.00}s == engaging");
                }
            }

            return (engaged, story);
        }
        finally
        {
            GameplayCVars.ModernAi.Value = previous;
        }
    }
}
