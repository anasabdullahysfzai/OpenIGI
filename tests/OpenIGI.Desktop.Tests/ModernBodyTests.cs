using System.Numerics;
using OpenIGI.Core.Graphics;
using OpenIGI.Presentation.Igi1;
using OpenIGI.Rendering.Headless;
using Xunit;

namespace OpenIGI.Desktop.Tests;

/// <summary>
/// The modern bodies on a real character: read whole, stood upright at a man's height, facing the way a soldier faces, and
/// walking on the spot so the soldier's own move carries him. The character is the play-test's, outside the repository.
/// </summary>
public sealed class ModernBodyTests(Xunit.Abstractions.ITestOutputHelper output)
{
    /// <summary>Where the character is: <c>OPENIGI_MODERN_BODY</c>, else the play-test's file beside the checkout.</summary>
    public static readonly string File = Environment.GetEnvironmentVariable("OPENIGI_MODERN_BODY")
        ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../animations/gltf/swat_guy_game.glb"));

    [RequiresModernBodyFact]
    public void TheCharacterReadsWholeWithBothClips()
    {
        using ModernBodyAsset asset = ModernBodyAsset.Load(new RecordingGraphicsDevice(), File);

        Assert.Equal(65, asset.Joints.Length);
        Assert.InRange(asset.Vertices.Length, 1000, ushort.MaxValue);
        Assert.Equal(2, asset.Subsets.Length);
        Assert.All(asset.Subsets, subset => Assert.True(subset.Diffuse.IsValid, "a subset has no texture"));
        Assert.InRange(asset.Idle.Duration, 5f, 12f);
        Assert.InRange(asset.Walk.Duration, 0.8f, 2f);
        Assert.InRange(asset.WalkMetresPerSecond, 0.9f, 1.5f);
    }

    [RequiresModernBodyFact]
    public void HeStandsUpAtAMansHeightFacingTheWayASoldierFaces()
    {
        using ModernBodyAsset asset = ModernBodyAsset.Load(new RecordingGraphicsDevice(), File);
        Vertex3D[] vertices = Skinned(asset, walkTime: 0f, walkWeight: 0f);

        float lowest = vertices.Min(v => v.Position.Z), highest = vertices.Max(v => v.Position.Z);
        Assert.InRange(highest - lowest, 1.6f, 2.0f);

        // A soldier faces +Y: the toes are ahead of the ankles.
        Vector3 toe = Joint(asset, "mixamorig:LeftToeBase", 0f, 0f), ankle = Joint(asset, "mixamorig:LeftFoot", 0f, 0f);
        Assert.True(toe.Y > ankle.Y + 0.05f, $"the toes are at {toe.Y:0.00}, the ankle at {ankle.Y:0.00}: he faces backwards");
    }

    [RequiresModernBodyFact]
    public void HeWalksOnTheSpot()
    {
        using ModernBodyAsset asset = ModernBodyAsset.Load(new RecordingGraphicsDevice(), File);
        Vector3 start = Joint(asset, "mixamorig:Hips", 0f, 1f);
        float furthest = 0f;
        for (int step = 1; step <= 20; step++)
        {
            Vector3 hips = Joint(asset, "mixamorig:Hips", asset.Walk.Duration * step / 20f, 1f);
            furthest = Math.Max(furthest, new Vector2(hips.X - start.X, hips.Y - start.Y).Length());
        }

        Assert.True(furthest < 0.15f, $"his hips wander {furthest:0.00} m in a stride: the walk still travels");
    }

    [RequiresModernBodyFact]
    public void EachFallEndsLyingWhereHeStoodAndStaysThere()
    {
        using ModernBodyAsset asset = ModernBodyAsset.Load(new RecordingGraphicsDevice(), File);
        int hips = Array.IndexOf(asset.Names, "mixamorig:Hips");
        foreach (bool behind in new[] { false, true })
        {
            foreach (bool head in new[] { false, true })
            {
                ModernClip? fall = asset.Deaths[behind ? 1 : 0, head ? 1 : 0];
                Assert.True(fall is not null, $"no fall for behind={behind} head={head}");
                Matrix4x4[] world = new Matrix4x4[asset.Rest.Length], skin = new Matrix4x4[asset.Joints.Length];
                asset.PoseFall(fall!, 0f, world, skin);
                Vector3 start = ModernBodyAsset.ToBody(world[hips].Translation, 0f);
                asset.PoseFall(fall!, fall!.Duration, world, skin);
                Vector3 end = ModernBodyAsset.ToBody(world[hips].Translation, 0f);
                Vertex3D[] lying = new Vertex3D[asset.Vertices.Length];
                asset.Skin(skin, 0f, 0f, lying);
                asset.PoseFall(fall, fall.Duration * 3f, world, skin);
                Vector3 later = ModernBodyAsset.ToBody(world[hips].Translation, 0f);

                float height = lying.Max(v => v.Position.Z) - lying.Min(v => v.Position.Z);
                float slide = new Vector2(end.X - start.X, end.Y - start.Y).Length();
                Assert.True(height < 0.8f, $"behind={behind} head={head}: {height:0.00} m tall at the end, not lying");
                Assert.True(slide < 0.05f, $"behind={behind} head={head}: the hips slide {slide:0.00} m");
                Assert.True(Vector3.Distance(end, later) < 1e-4f, $"behind={behind} head={head}: does not hold its last frame");
            }
        }
    }

    [RequiresModernBodyFact]
    public void EachFlinchPushesHisHeadAwayFromTheShooter()
    {
        using ModernBodyAsset asset = ModernBodyAsset.Load(new RecordingGraphicsDevice(), File);
        int head = Array.IndexOf(asset.Names, "mixamorig:Head");
        Matrix4x4[] world = new Matrix4x4[asset.Rest.Length], skin = new Matrix4x4[asset.Joints.Length];
        Vector3 Head(ModernClip hit, float time)
        {
            asset.PoseFall(hit, time, world, skin);
            return ModernBodyAsset.ToBody(world[head].Translation, 0f);
        }

        // Shooter ahead, behind, to the left, to the right, in the body's frame (facing +Y, right +X): the head goes the other way.
        foreach ((string name, float ahead, float right) in new[] { ("front", 1f, 0f), ("back", -1f, 0f), ("left", 0f, -1f), ("right", 0f, 1f) })
        {
            ModernClip hit = asset.Hit(ahead, right)!;
            Vector3 start = Head(hit, 0f), furthest = start;
            for (int step = 1; step <= 30; step++)
            {
                Vector3 at = Head(hit, hit.Duration * step / 30f);
                if (Vector2.Distance(new(at.X, at.Y), new(start.X, start.Y)) > Vector2.Distance(new(furthest.X, furthest.Y), new(start.X, start.Y)))
                {
                    furthest = at;
                }
            }

            Vector2 push = new(furthest.X - start.X, furthest.Y - start.Y);
            float along = -((push.X * right) + (push.Y * ahead));
            output.WriteLine($"{name}: {hit.Duration:0.00} s, head pushed ({push.X:0.00}, {push.Y:0.00}) m, {along:0.00} m away from the shooter");
            Assert.True(along > 0.05f, $"{name}: the head goes {along:0.00} m away from the shooter");
        }
    }

    // The posed mesh in the soldier's frame.
    private static Vertex3D[] Skinned(ModernBodyAsset asset, float walkTime, float walkWeight)
    {
        Matrix4x4[] world = new Matrix4x4[asset.Rest.Length], skin = new Matrix4x4[asset.Joints.Length];
        asset.Pose(0f, walkTime, walkWeight, world, skin);
        Vertex3D[] vertices = new Vertex3D[asset.Vertices.Length];
        asset.Skin(skin, 0f, 0f, vertices);
        return vertices;
    }

    // A joint's place in the soldier's frame.
    private static Vector3 Joint(ModernBodyAsset asset, string name, float walkTime, float walkWeight)
    {
        Matrix4x4[] world = new Matrix4x4[asset.Rest.Length], skin = new Matrix4x4[asset.Joints.Length];
        asset.Pose(0f, walkTime, walkWeight, world, skin);
        int node = Array.IndexOf(asset.Names, name);
        Assert.True(node >= 0, $"no joint named {name}");
        return ModernBodyAsset.ToBody(world[node].Translation, 0f);
    }
}

/// <summary>A fact that needs the play-test character, skipped where it is not.</summary>
public sealed class RequiresModernBodyFactAttribute : FactAttribute
{
    /// <summary>Initializes a new instance of the <see cref="RequiresModernBodyFactAttribute"/> class.</summary>
    public RequiresModernBodyFactAttribute()
    {
        if (!System.IO.File.Exists(ModernBodyTests.File))
        {
            Skip = $"No modern character at {ModernBodyTests.File} (set OPENIGI_MODERN_BODY).";
        }
    }
}
