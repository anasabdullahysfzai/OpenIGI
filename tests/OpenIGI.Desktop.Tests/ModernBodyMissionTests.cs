using OpenIGI.Core.Graphics;
using OpenIGI.Engine.Flow;
using OpenIGI.Engine.Level;
using OpenIGI.Engine.Resources;
using OpenIGI.Engine.Tasks;
using OpenIGI.Engine.Time;
using OpenIGI.Engine.Vfs;
using OpenIGI.Formats.Ai;
using OpenIGI.Game;
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
        }
        finally
        {
            GameplayCVars.ModernBodies.Value = on;
            GameplayCVars.ModernBodyFile.Value = file;
        }
    }

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
