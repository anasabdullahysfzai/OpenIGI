using OpenIGI.Core.Numerics;
using OpenIGI.Game.Ai;
using OpenIGI.Game.Igi1.Ai;
using OpenIGI.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace OpenIGI.Game.Tests;

/// <summary>Hunting for a player lost, and a team-mate found dead, in the test room (<see cref="ModernArena"/>). <b>Not retail.</b></summary>
[Collection(ModernAiTests.Name)]
public sealed class ModernSearchArenaTests(ITestOutputHelper output)
{
    /// <summary>
    /// A squad in a fight that loses the player hunts for him where he was last seen, each from nodes of his own, one down on a knee at
    /// the last; and goes back to his patrol still on edge.
    /// </summary>
    [RequiresGameDataFact]
    public void ASquadThatLosesThePlayerHuntsForHimAndStaysOnEdge()
    {
        With(() =>
        {
            ModernArena arena = new ModernArena(60.0).Box(0, 40, 0, 60, 41, 4.0);
            arena.Build();
            List<string> story = [];
            int tick = 0;
            List<Igi1HumanAi> guards = [];
            foreach ((double x, int id) in new[] { (22.0, 1), (30.0, 2), (38.0, 3) })
            {
                int n = id;
                guards.Add(arena.Patrol(ModernArena.At(x, 12), 0f, text => story.Add($"{tick / 30.0:00.0}s guard {n}: {text}"), seed: id + 10));
            }

            Vector3d player = ModernArena.At(30, 30);
            bool scanned = false;
            HashSet<int>[] places = [[], [], []];
            for (tick = 1; tick <= 90 * 30; tick++)
            {
                // In the open until they are all fighting, then right up to the wall, and round behind it.
                if (tick == 10 * 30)
                {
                    player = ModernArena.At(30, 38);
                }
                else if (tick == 11 * 30)
                {
                    player = ModernArena.At(30, 43);
                }

                for (int g = 0; g < guards.Count; g++)
                {
                    arena.Tick(guards[g], tick, player);
                    scanned |= guards[g].KneelScanning;
                    if (guards[g].IsSearching && guards[g].DestinationNode >= 0)
                    {
                        places[g].Add(guards[g].DestinationNode);
                    }
                }
            }

            output.WriteLine(string.Join("\n", story.Where(line => !line.Contains("sight", StringComparison.Ordinal) && !line.Contains("sees the", StringComparison.Ordinal))));
            output.WriteLine($"places: {string.Join(" | ", places.Select(spots => string.Join(",", spots)))}");
            Assert.Equal(3, story.Count(line => line.Contains("lost him; hunts", StringComparison.Ordinal)));
            Assert.True(scanned, "nobody knelt to look about");
            Assert.All(places, spots => Assert.True(spots.Count >= 3, "he did not walk his search"));
            Assert.True(places[0].Intersect(places[1]).Count() < places[0].Count, "they searched the same places");
            Assert.Equal(3, story.Count(line => line.Contains("search over", StringComparison.Ordinal)));
            Assert.All(guards, guard => Assert.True(guard.IsOnEdge, "he calmed down at once"));
        });
    }

    /// <summary>
    /// A guard who sees a team-mate down goes and checks him and waves the squad on; another nearby, who never saw him, answers. A body
    /// behind a guard is not noticed.
    /// </summary>
    [RequiresGameDataFact]
    public void AGuardFindsABodyChecksItAndCallsTheSquad()
    {
        With(() =>
        {
            ModernArena arena = new ModernArena(60.0).Box(0, 40, 0, 60, 41, 4.0);
            arena.Build();
            List<string> story = [];
            int tick = 0;
            Igi1HumanAi finder = arena.Patrol(ModernArena.At(30, 10), 0f, text => story.Add($"{tick / 30.0:00.0}s finder: {text}"), seed: 21);
            Igi1HumanAi other = arena.Patrol(ModernArena.At(44, 10), 0f, text => story.Add($"{tick / 30.0:00.0}s other: {text}"), seed: 22);
            Vector3d player = ModernArena.At(30, 50), body = ModernArena.At(30, 18);
            for (tick = 1; tick <= 60 * 30; tick++)
            {
                arena.Tick(finder, tick, player, body);
                arena.Tick(other, tick, player);
            }

            output.WriteLine(string.Join("\n", story));
            Assert.Equal((1, 1), finder.SearchGestures);
            int saw = story.FindIndex(line => line.Contains("finder: sees a team-mate down", StringComparison.Ordinal));
            int check = story.FindIndex(line => line.Contains("finder: checks the body", StringComparison.Ordinal));
            int wave = story.FindIndex(line => line.Contains("finder: waves the squad on", StringComparison.Ordinal));
            Assert.InRange(saw, 0, check);
            Assert.InRange(check, 0, wave);
            Assert.Contains(story, line => line.Contains("other: lost him; hunts", StringComparison.Ordinal));

            // The same body behind a lone guard.
            ModernArena second = new ModernArena(60.0).Box(0, 40, 0, 60, 41, 4.0);
            second.Build();
            Igi1HumanAi blind = second.Patrol(ModernArena.At(30, 20), 0f, _ => { }, seed: 23);
            for (tick = 1; tick <= 15 * 30; tick++)
            {
                second.Tick(blind, tick, player, ModernArena.At(30, 12));
            }

            Assert.Equal((0, 0), blind.SearchGestures);
            Assert.False(blind.IsOnEdge, "he noticed a body behind him");
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
