using OpenIGI.Core.Numerics;
using OpenIGI.Game.Igi1.Ai;
using OpenIGI.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace OpenIGI.Game.Tests;

/// <summary>What keeps a fight fair, in the test room (<see cref="ModernArena"/>). <b>Not retail.</b></summary>
[Collection(ModernAiTests.Name)]
public sealed class ModernFairnessArenaTests(ITestOutputHelper output)
{
    private const double Metre = ModernArena.Metre;

    /// <summary>Six guards with a clear view of a man fire no more than three at a time, and his aim comes with tracking him.</summary>
    [RequiresGameDataFact]
    public void OnlySoManyFireAtOnceAndAimComesWithTracking()
    {
        With(() =>
        {
            ModernArena arena = new(60.0);
            arena.Build();
            List<Igi1HumanAi> guards = [];
            foreach (int id in Enumerable.Range(1, 6))
            {
                guards.Add(arena.Patrol(ModernArena.At(10 + (id * 7), 15), 0f, _ => { }, seed: id + 40));
            }

            Vector3d player = ModernArena.At(30, 32);
            int mostFiring = 0, mostTriggers = 0, fights = 0;
            double firstShare = -1.0, lastShare = 0.0;
            for (int tick = 1; tick <= 30 * 30; tick++)
            {
                int triggers = 0;
                foreach (Igi1HumanAi guard in guards)
                {
                    arena.Tick(guard, tick, player);
                    triggers += guard.Combat.PullTrigger ? 1 : 0;
                    if (firstShare < 0.0 && guard.Combat.PullTrigger)
                    {
                        firstShare = guard.TrackingShare;
                    }
                }

                fights = guards.Count(guard => guard.HasCombatRecord);
                mostFiring = Math.Max(mostFiring, arena.Squad.FiringNow(tick));
                mostTriggers = Math.Max(mostTriggers, triggers);
                lastShare = guards.Max(guard => guard.TrackingShare);
            }

            output.WriteLine($"fighting {fights}, most with a turn {mostFiring}, most on the trigger {mostTriggers}, aim at first shot {firstShare:0.00}, later {lastShare:0.00}");
            Assert.True(fights >= 4, $"only {fights} fighting");
            Assert.InRange(mostFiring, 1, AiSquad.MostFiring);
            Assert.InRange(mostTriggers, 1, AiSquad.MostFiring);
            Assert.InRange(firstShare, Igi1HumanAi.FirstAimShare, 0.75);
            Assert.Equal(1.0, lastShare, 2);
        });
    }

    /// <summary>
    /// A gunshot gives a guess, wrong by something and within bounds; and what a squad mate sees reaches a guard nearby but not one far
    /// off, who goes on his guess.
    /// </summary>
    [RequiresGameDataFact]
    public void ASoundGivesAGuessAndWordOnlyCarriesSoFar()
    {
        With(() =>
        {
            ModernArena arena = new ModernArena(60.0).Box(0, 28, 0, 60, 29, 4.0);
            arena.Build();
            Vector3d player = ModernArena.At(30, 25);
            Igi1HumanAi seer = arena.Patrol(ModernArena.At(30, 12), 0f, _ => { }, seed: 51);

            // Both behind the wall, out of his sight: one by the seer, one beyond word of him.
            Igi1HumanAi near = arena.Patrol(ModernArena.At(30, 32), 0f, _ => { }, seed: 52);
            Igi1HumanAi far = arena.Patrol(ModernArena.At(30, 56), 0f, _ => { }, seed: 53);
            double farEarly = -1.0;
            for (int tick = 1; tick <= 8 * 30; tick++)
            {
                arena.Tick(seer, tick, player);
                arena.Tick(near, tick, player);
                arena.Tick(far, tick, player);
                if (tick == 30)
                {
                    near.HearGunshot(player, 200.0 * Metre, false, AiDetectionCandidate.PlayerId);
                    far.HearGunshot(player, 200.0 * Metre, false, AiDetectionCandidate.PlayerId);
                }

                // Far beyond word, he goes on his guess; he may come within word of the seer later, walking towards it.
                if (tick == 60)
                {
                    farEarly = far.LastKnownForTesting is { } known ? Vector3d.Distance(known, player) / Metre : -1.0;
                }
            }

            double Off(Igi1HumanAi guard) => guard.LastKnownForTesting is { } known ? Vector3d.Distance(known, player) / Metre : -1.0;
            output.WriteLine($"seer fighting {seer.HasCombatRecord}, near fighting {near.HasCombatRecord} off by {Off(near):0.0} m, far fighting {far.HasCombatRecord} off by {farEarly:0.0} m early, {Off(far):0.0} m later");
            Assert.True(seer.HasCombatRecord, "the guard who saw him did not fight");
            Assert.True(near.HasCombatRecord && far.HasCombatRecord, "the shot did not bring them in");
            Assert.InRange(Off(near), 0.0, 0.5); // told by the seer, 20 m off
            Assert.InRange(farEarly, 0.5, Igi1HumanAi.SoundGuessMostMetres + 0.1); // 44 m from the seer: his guess
        });
    }

    /// <summary>A guard called into a fight unseeing, on his way to a position, stops and shoots when the man comes into view; once.</summary>
    [RequiresGameDataFact]
    public void AGuardOnHisWayStopsAndShootsAtTheFirstSightOfHim()
    {
        With(() =>
        {
            ModernArena arena = new ModernArena(60.0).Box(0, 40, 0, 60, 41, 4.0);
            arena.Build();
            List<string> story = [];
            int tick = 0, shotAt = -1, cameIntoView = -1;
            Igi1HumanAi guard = arena.Patrol(ModernArena.At(30, 12), 0f, text => story.Add($"{tick / 30.0:00.0}s {text}"));
            Vector3d player = ModernArena.At(30, 43);
            bool Moving() => guard.CoverState is "Moving" or "Advancing";
            for (tick = 1; tick <= 30 * 30 && shotAt < 0; tick++)
            {
                arena.Tick(guard, tick, player);
                if (tick == 60)
                {
                    guard.ReceiveAlert(guard.Position + new Vector3d(3.0 * Metre, 0.0, 0.0));
                }

                // Round the wall and into the room as soon as he is on his way.
                if (cameIntoView < 0 && Moving())
                {
                    (player, cameIntoView) = (ModernArena.At(40, 30), tick);
                }

                if (cameIntoView >= 0 && guard.Combat.PullTrigger)
                {
                    shotAt = tick;
                }
            }

            output.WriteLine(string.Join("\n", story));
            Assert.True(cameIntoView > 0, "he never set off");
            Assert.Single(story, line => line.Contains("shoots first", StringComparison.Ordinal));
            Assert.InRange(shotAt - cameIntoView, 0, 60);
        });
    }

    /// <summary>
    /// A guard who is sure of a man running in plain view shoots first, rather than running to a place to shoot from: within a moment
    /// and a step or two of where he stood, but for those waiting on a turn to fire.
    /// </summary>
    [RequiresGameDataFact]
    public void AGuardWhoSpotsAManInViewShootsBeforeHeRuns()
    {
        With(() =>
        {
            foreach ((string name, bool cover) in new[] { ("open room", false), ("room with cover", true) })
            {
                ModernArena arena = new(60.0);
                if (cover)
                {
                    arena.Box(14, 20, 0, 18, 20.6, 1.0).Box(28, 20, 0, 32, 20.6, 1.0).Box(42, 20, 0, 46, 20.6, 1.0).Box(21, 24, 0, 22.5, 25.5, 3.0).Box(37, 24, 0, 38.5, 25.5, 3.0);
                }

                arena.Build();
                List<Igi1HumanAi> guards = [];
                List<(int Tick, string Text)>[] logs = [[], [], [], []];
                int tick = 0;
                foreach (int id in Enumerable.Range(0, 4))
                {
                    int n = id;
                    guards.Add(arena.Patrol(ModernArena.At(14 + (id * 11), 12), 0f, text => logs[n].Add((tick, text)), seed: id + 60));
                }

                int[] sure = [-1, -1, -1, -1], firstShot = [-1, -1, -1, -1], ranSeen = [0, 0, 0, 0];
                Vector3d[] sureAt = new Vector3d[4];
                double[] ran = new double[4];
                int strafingShots = 0;
                for (tick = 1; tick <= 25 * 30; tick++)
                {
                    // Running across the room in the open, past them.
                    Vector3d player = ModernArena.At(30 + (24 * Math.Sin(tick * 3.2 / 30.0 / 24.0)), 33);
                    for (int g = 0; g < 4; g++)
                    {
                        Vector3d before = guards[g].Position;
                        arena.Tick(guards[g], tick, player);
                        if (sure[g] < 0 && guards[g].HasCombatRecord)
                        {
                            (sure[g], sureAt[g]) = (tick, guards[g].Position);
                        }

                        if (sure[g] >= 0 && firstShot[g] < 0)
                        {
                            ran[g] = Vector3d.Distance(guards[g].Position, sureAt[g]) / Metre;
                            if (guards[g].Combat.PullTrigger)
                            {
                                firstShot[g] = tick;
                            }
                        }

                        if (guards[g].HasCombatRecord && Vector3d.Distance(before, guards[g].Position) > 0.05 * Metre && guards[g].WatchingPlayer && !guards[g].Combat.PullTrigger)
                        {
                            ranSeen[g]++;
                        }

                        strafingShots += guards[g].Strafing && guards[g].Combat.PullTrigger ? 1 : 0;
                    }
                }

                for (int g = 0; g < 4; g++)
                {
                    output.WriteLine($"{name} guard {g}: sure at {sure[g] / 30.0:0.0} s, to first shot {(firstShot[g] < 0 ? "never" : $"{(firstShot[g] - sure[g]) / 30.0:0.0} s")}, ran {ran[g]:0.0} m first; moving while he sees him and not firing {ranSeen[g] / 30.0:0.0} s");
                    foreach ((int at, string text) in logs[g].Where(l => !l.Text.Contains("sight", StringComparison.Ordinal) && !l.Text.Contains("sees", StringComparison.Ordinal) && !l.Text.StartsWith("now:", StringComparison.Ordinal)).Take(14))
                    {
                        output.WriteLine($"    {at / 30.0:00.0}s {text}");
                    }
                }

                // Anyone with time left to fire does so at once, from about where he stood: but one, who may be waiting on a turn.
                int[] inTime = Enumerable.Range(0, 4).Where(g => sure[g] >= 0 && sure[g] < 20 * 30).ToArray();
                output.WriteLine($"{name}: ticks on the trigger while strafing {strafingShots}");
                Assert.True(inTime.Length >= 2, $"{name}: only {inTime.Length} sure in time");
                Assert.True(inTime.Count(g => firstShot[g] >= 0 && firstShot[g] - sure[g] <= 45 && ran[g] <= 2.0) >= inTime.Length - 1, $"{name}: slow to shoot");
                Assert.All(inTime, g => Assert.True(firstShot[g] >= 0, $"{name}: guard {g} never fired"));
                Assert.True(strafingShots > 0, $"{name}: nobody fired on the move");
            }
        });
    }

    /// <summary>
    /// A guard who has had his first shot, on a run he set off on with the player out of sight: the player steps out ahead of him, and he
    /// stops and shoots, rather than running on past him as if he were not there.
    /// </summary>
    [RequiresGameDataFact]
    public void AGuardOnARunStopsWheneverAManComesIntoView()
    {
        With(() =>
        {
            ModernArena arena = new ModernArena(70.0).Box(0, 34, 0, 70, 35, 4.0);
            arena.Build();
            List<string> story = [];
            int tick = 0;
            Igi1HumanAi guard = arena.Patrol(ModernArena.At(35, 6), 0f, text => story.Add($"{tick / 30.0:00.0}s {text}"), seed: 11);
            Vector3d hidden = ModernArena.At(35, 60), player = ModernArena.At(35, 20);
            int hideAt = -1, running = 0, cameOutAt = -1, stoppedAt = -1, shotAt = -1;
            Vector3d cameOutFrom = default, stoppedAtPlace = default;
            for (tick = 1; tick <= 60 * 30 && (stoppedAt < 0 || shotAt < 0); tick++)
            {
                arena.Tick(guard, tick, player);
                bool runs = guard.CoverState is "Moving" or "Advancing" && guard.WalksForward;
                if (hideAt < 0 && story.Exists(line => line.Contains("shoots first", StringComparison.Ordinal)))
                {
                    hideAt = tick + 75;
                }
                else if (hideAt > 0 && tick == hideAt)
                {
                    player = hidden;
                }
                else if (hideAt > 0 && tick > hideAt && cameOutAt < 0)
                {
                    running = runs ? running + 1 : 0;
                    if (running >= 12)
                    {
                        Vector3f ahead = guard.Orientation.Column2;
                        player = guard.Position + (new Vector3d(ahead.X, ahead.Y, 0.0) * (9.0 * Metre));
                        (cameOutAt, cameOutFrom) = (tick, guard.Position);
                    }
                }
                else if (cameOutAt > 0)
                {
                    if (!runs && stoppedAt < 0)
                    {
                        (stoppedAt, stoppedAtPlace) = (tick, guard.Position);
                    }

                    shotAt = shotAt < 0 && guard.Combat.PullTrigger ? tick : shotAt;
                }
            }

            output.WriteLine(string.Join("\n", story));
            output.WriteLine($"came out at {cameOutAt}, stopped at {stoppedAt}, shot at {shotAt}");
            Assert.True(cameOutAt > 0, "he never set off");
            Assert.InRange(stoppedAt - cameOutAt, 0, 15);
            Assert.True(Vector3d.Distance(cameOutFrom, stoppedAtPlace) < 2.0 * Metre, "he ran on past him");
            Assert.InRange(shotAt - cameOutAt, 0, 60);
        });
    }

    /// <summary>A guard pinned in the open, running for cover with the player in sight, keeps going, but with his rifle on him and firing as he goes.</summary>
    [RequiresGameDataFact]
    public void AGuardRunningForCoverFiresOnTheManHeCanSee()
    {
        With(() =>
        {
            ModernArena arena = new ModernArena(60.0).Box(44, 8, 0, 47, 16, 4.0);
            arena.Build();
            List<string> story = [];
            int tick = 0, strafing = 0, firing = 0;
            Igi1HumanAi guard = arena.Patrol(ModernArena.At(30, 12), 0f, text => story.Add($"{tick / 30.0:00.0}s {text}"), seed: 11);
            Vector3d player = ModernArena.At(30, 30);
            int ranAt = -1;
            for (tick = 1; tick <= 30 * 30; tick++)
            {
                arena.Tick(guard, tick, player);
                ranAt = ranAt < 0 && story.Exists(line => line.Contains("runs for cover", StringComparison.Ordinal)) ? tick : ranAt;
                if (ranAt < 0 && tick % 6 == 0)
                {
                    arena.Shoot(guard, player, guard.Position + new Vector3d(1.0 * Metre, 0.0, 1.2 * Metre));
                }
                else if (ranAt > 0 && guard.CoverState == "Moving" && guard.WalksForward)
                {
                    strafing += guard.Strafing ? 1 : 0;
                    firing += guard.Combat.PullTrigger ? 1 : 0;
                }
                else if (ranAt > 0 && tick > ranAt + 1)
                {
                    break;
                }
            }

            output.WriteLine(string.Join("\n", story));
            output.WriteLine($"ran at {ranAt}: strafing {strafing} ticks, firing {firing}");
            Assert.True(ranAt > 0, "nothing pinned him into a run for cover");
            Assert.True(strafing > 10, "he turned his back on the man he could see");
            Assert.True(firing > 0, "he never fired on the way");
        });
    }

    private static void With(Action run)
    {
        bool previous = GameplayCVars.ModernAi.Value;
        GameplayCVars.ModernAi.Value = true;
        try
        {
            run();
        }
        finally
        {
            GameplayCVars.ModernAi.Value = previous;
        }
    }
}
