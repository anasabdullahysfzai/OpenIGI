using OpenIGI.Core.Graphics;
using OpenIGI.Engine.Flow;
using OpenIGI.Engine.Level;
using OpenIGI.Engine.Resources;
using OpenIGI.Engine.Tasks;
using OpenIGI.Engine.Time;
using OpenIGI.Engine.Vfs;
using OpenIGI.Core.Numerics;
using OpenIGI.Formats.Ai;
using OpenIGI.Game;
using OpenIGI.Game.Igi1.Ai;
using OpenIGI.Game.Player;
using OpenIGI.Game.World;
using OpenIGI.Presentation.Igi1;
using OpenIGI.Rendering.Headless;
using OpenIGI.TestSupport;
using Xunit;

namespace OpenIGI.Desktop.Tests;

/// <summary>The modern bodies on mission 1's own soldiers: each given one, skinned where his own model stands.</summary>
[Collection(ModernAiMissionTests.Name)]
public sealed class ModernBodyMissionTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [RequiresGameDataFact]
    public void EachSoldiersModernBodyStandsWhereHisOwnModelDoes()
    {
        if (!File.Exists(ModernBodyTests.File))
        {
            return;
        }

        (bool on, string file) = (GameplayCVars.ModernBodies.Value, GameplayCVars.ModernBodyFile.Value);
        GameplayCVars.ModernBodies.Value = true;
        GameplayCVars.ModernBodyFile.Value = ModernBodyTests.File;
        try
        {
            RecordingGraphicsDevice device = new();
            using SoldierRenderer renderer = Load(device, 1);
            for (int tick = 0; tick < 10; tick++)
            {
                renderer.Tick();
            }

            List<string> wrong = [];
            foreach ((SoldierMember member, float lowest) in renderer.MembersForTesting.Where(m => !m.Member.Agent.View.IsDead).Take(5))
            {
                Assert.NotNull(member.Modern);
                member.Modern!.SkinIfStale(); // As the view about to draw him does: a tick only poses.
                Vertex3D[] modern = device.Meshes[member.Modern!.Mesh.Id].Vertices;
                string line = $"{member.Agent.View.Soldier.Id}: model's lowest {lowest:0.00} m | modern {Bounds(modern)} | lift {member.Modern.Lift}";
                output.WriteLine(line);

                // In the soldier's frame, metres: his feet on his model's lowest vertex, a man's height, over his origin.
                float low = modern.Min(v => v.Position.Z), high = modern.Max(v => v.Position.Z);
                float midX = modern.Average(v => v.Position.X), midY = modern.Average(v => v.Position.Y);
                if (Math.Abs(low - lowest) > 0.05f || high - low is < 1.5f or > 2.1f || Math.Abs(midX) > 0.5f || Math.Abs(midY) > 0.5f)
                {
                    wrong.Add(line);
                }
            }

            Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));

            // Shot from behind: he keeps the body, falls in it, and lies there.
            SoldierMember shot = renderer.MembersForTesting.Select(m => m.Member).First(m => m.Modern is not null);
            Vertex3D[] standing = device.Meshes[shot.Modern!.Mesh.Id].Vertices;
            double floor = shot.TickWhere.Z + standing.Min(v => v.Position.Z);
            shot.Agent.View.KillShot = (true, false);
            shot.Agent.View.Kill(Falls.First(id => shot.Agent.View.Bones.Set.TryGet(id, out _)));
            for (int tick = 0; tick < 150; tick++)
            {
                renderer.Tick();
            }

            Assert.True(shot.Modern is { Fallen: true }, "the dead guard lost his modern body");
            Vertex3D[] lying = device.Meshes[shot.Modern!.Mesh.Id].Vertices;
            output.WriteLine($"dead: {Bounds(lying)}");
            Assert.True(lying.Max(v => v.Position.Z) - lying.Min(v => v.Position.Z) < 0.8f, $"not lying: {Bounds(lying)}");
            double corpseLowest = shot.TickWhere.Z + lying.Min(v => v.Position.Z);
            output.WriteLine($"root now {shot.Agent.View.RootSample.Z:0} units, clip {shot.Agent.View.CurrentAnimation}, time {shot.Agent.View.Bones.SourceOf(0)?.Time}, drawn at {shot.TickWhere.Z:0.00} m");
            output.WriteLine($"floor {floor:0.00} m, corpse's lowest {corpseLowest:0.00} m");
            Assert.True(Math.Abs(corpseLowest - floor) < 0.03, $"the corpse's lowest point is {corpseLowest - floor:0.00} m off the floor");
        }
        finally
        {
            GameplayCVars.ModernBodies.Value = on;
            GameplayCVars.ModernBodyFile.Value = file;
        }
    }

    [Theory]
    [InlineData(null, 0.0, true)]              // Open and level.
    [InlineData(1.5, 0.0, false)]              // A wall in the way, ahead of him.
    [InlineData(-0.8, 0.0, false)]             // A wall behind him, where the attacker starts.
    [InlineData(null, 0.5, false)]             // A step down ahead of him.
    [InlineData(null, 0.2, true)]              // A slight rise is nothing.
    public void ATakedownNeedsRoomAndLevelGround(double? wallAhead, double step, bool fits)
    {
        const double M = WorldScene.UnitsPerMetre;
        Vector3d at = new(100.0 * M, 200.0 * M, 10.0 * M);

        // He faces +Y (facing 0). A wall across the way at so many metres along Y from him, and the ground changing beyond one metre ahead.
        bool Walls(Vector3d from, Vector3d to, out WorldRayHit hit)
        {
            hit = default;
            return wallAhead is { } wall && (from.Y - at.Y - (wall * M)) * (to.Y - at.Y - (wall * M)) <= 0.0;
        }

        double? Ground(double x, double y, double from) => (y - at.Y > M ? -step : 0.0) * M + at.Z;
        Assert.Equal(fits, TakedownRoom.Fits(at, 0f, Walls, Ground));
    }

    [RequiresGameDataFact]
    public void ATakedownPlaysThePairFromTheVictimsOriginAndEndsCleanly()
    {
        if (!File.Exists(ModernBodyTests.File))
        {
            return;
        }

        (bool on, string file) = (GameplayCVars.ModernBodies.Value, GameplayCVars.ModernBodyFile.Value);
        GameplayCVars.ModernBodies.Value = true;
        GameplayCVars.ModernBodyFile.Value = ModernBodyTests.File;
        try
        {
            RecordingGraphicsDevice device = new();
            using SoldierRenderer renderer = Load(device, 1);
            for (int tick = 0; tick < 10; tick++)
            {
                renderer.Tick();
            }

            SoldierMember victim = renderer.MembersForTesting.Select(m => m.Member).First(m => m.Modern is not null);
            TakedownRun run = new(victim.Agent.View, Camera3D.Default);
            renderer.Simulation.Takedown = run;
            for (run.Tick = 1; run.Tick <= 20; run.Tick++)
            {
                renderer.Tick();
            }

            run.Tick = 20;
            ModernBody? attacker = victim.TakedownPartner;
            Assert.NotNull(attacker);
            Assert.True(attacker!.ShowsKnife);
            Assert.True(victim.Modern!.Fallen);
            Assert.NotNull(run.End);

            // The eye is the attacker's, behind the victim and at a man's height: the clip's root offset was kept, not taken out as a fall's slide.
            Vector3d eye = run.Eye!(1f).Position - victim.DrawnWhere;
            double behind = Math.Sqrt((eye.X * eye.X) + (eye.Y * eye.Y));

            // He is drawn from his root, its height above his feet in his root sample.
            double aboveFeet = eye.Z + (victim.Agent.View.RootSample.Z / WorldScene.UnitsPerMetre);
            output.WriteLine(FormattableString.Invariant($"eye {behind:0.00} m from him, {aboveFeet:0.00} m above his feet"));
            Assert.InRange(behind, 0.4, 0.9);
            Assert.InRange(aboveFeet, 1.4, 1.9);

            renderer.Simulation.Takedown = null;
            renderer.Tick();

            // Ended with him still alive (the player was shot dead): the attacker goes, and he is himself again, standing.
            Assert.Null(victim.TakedownPartner);
            Assert.False(victim.TakenDown);
            Assert.False(victim.Modern is { Fallen: true });
        }
        finally
        {
            GameplayCVars.ModernBodies.Value = on;
            GameplayCVars.ModernBodyFile.Value = file;
        }
    }

    // The standing-to-ground falls a soldier class names (SoldierDeath).
    private static readonly int[] Falls = [71, 28, 22, 23, 30];

    private static string Bounds(Vertex3D[] vertices) => FormattableString.Invariant(
        $"x {vertices.Min(v => v.Position.X):0.00}..{vertices.Max(v => v.Position.X):0.00} y {vertices.Min(v => v.Position.Y):0.00}..{vertices.Max(v => v.Position.Y):0.00} z {vertices.Min(v => v.Position.Z):0.00}..{vertices.Max(v => v.Position.Z):0.00}");

    private static SoldierRenderer Load(RecordingGraphicsDevice device, int level)
    {
        VirtualFileSystem fileSystem = new();
        fileSystem.RegisterDevice(new LocalFileDevice(GameData.RequireRoot()));
        ResourceRegistry resources = new(fileSystem);
        GameLoop loop = new(new ManualTimeSource());
        PlayerTaskTypes.RegisterAll(loop.Types);
        MissionRegistry registry = new();
        registry.Register(loop.Natives);
        registry.Scan(fileSystem, resources, loop.Natives);
        LevelTask root = (LevelTask)loop.Tasks.New(loop.Tasks.Root, loop.Types.Get(EngineTaskTypes.Level));
        LoadedMission mission = new MissionLoader(registry, fileSystem, resources, new LevelLoader(root, loop.TaskCommands, loop.Natives, resources))
            .Load(level);
        SoldierRenderer renderer = new(device, resources);
        Assert.True(renderer.Load(mission, root) > 0);
        Assert.True(renderer.Simulation.Animate(new Dictionary<int, GraphFile>(), levelRoot: root) > 0);
        return renderer;
    }
}
