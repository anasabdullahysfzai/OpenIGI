using OpenIGI.Core.Diagnostics;
using OpenIGI.Core.Graphics;
using OpenIGI.Game;
using OpenIGI.Game.Igi1.Ai;
using OpenIGI.Game.Igi1.Player;
using OpenIGI.Game.World;

namespace OpenIGI.Presentation.Igi1;

/// <content>
/// The modern bodies (<see cref="GameplayCVars.ModernBodies"/>): a glTF character read once, and each living soldier given his
/// own posed copy of it, which the drawer draws in his own model's place. <b>Not retail.</b>
/// </content>
public sealed partial class SoldierRenderer
{
    /// <summary>Gets every soldier the renderer draws, with his model's lowest vertex in metres, for testing.</summary>
    internal IEnumerable<(SoldierMember Member, float Lowest)> MembersForTesting =>
        _groups.SelectMany(group => group.Soldiers.Select(member => (member, group.Body.LowestVertexZ)));

    private ModernBodyAsset? _modernAsset;
    private string? _modernAssetPath;
    private bool _modernAssetFailed;
    private readonly Dictionary<SoldierMember, double> _modernFallHeights = [];
    private readonly Dictionary<SoldierMember, double> _modernStandingHeights = [];
    private readonly Dictionary<SoldierMember, float> _modernRootTops = [];
    private readonly Dictionary<SoldierMember, int> _modernHitsSeen = [];

    /// <summary>Gives a soldier his modern body for this tick, or takes it away.</summary>
    /// <param name="group">The soldier's group, whose model stands him on the ground.</param>
    /// <param name="member">The soldier.</param>
    private void TickModernBody(SoldierGroup group, SoldierMember member)
    {
        SoldierView view = member.Agent.View;
        if (ModernAsset() is not { } asset || (view.IsDead && member.Modern is not { Fallen: true } && !StartFall(member, asset, view)))
        {
            // Dead with no fall to play, or one that came back to life (a respawn): his own model, or a fresh body.
            DropModernBody(member);
            return;
        }

        string forced = GameplayCVars.ModernPose.Value;
        if (!view.IsDead && member.Modern is { Fallen: true } && !forced.StartsWith("die", StringComparison.Ordinal))
        {
            DropModernBody(member);
        }

        if (member.Modern is null)
        {
            // His feet where his own model's are: its lowest vertex, in metres, which sets the body on the ground. (Not his
            // skin scratch: until his first skinning succeeds that holds the model's raw geometry, in its own units.)
            member.Modern = new ModernBody(asset, _device);
            member.Modern.MatchLowest(group.Body.LowestVertexZ);
            _modernHitsSeen[member] = view.LastHit.Count;
        }

        ModernBody body = member.Modern;
        if (forced.Length > 0 && !view.IsDead)
        {
            TickForcedPose(member, body, asset, forced);
            return;
        }

        float raise = 0f;
        if (!view.IsDead)
        {
            // A soldier with a brain is drawn at his clip's root height above the ground. His own flinch, kneel or crouch brings
            // that down; the character only stands and walks, so the drop is given back and he stays on his feet. (Measured from
            // the highest his root has stood, which is standing.)
            if (_sim.Brains.TryGetValue(view, out Igi1HumanAi? brain) && brain.HasGroundQuery)
            {
                float top = Math.Max(_modernRootTops.GetValueOrDefault(member, view.RootSample.Z), view.RootSample.Z);
                _modernRootTops[member] = top;
                raise = (top - view.RootSample.Z) / (float)WorldScene.UnitsPerMetre;
            }

            _modernStandingHeights[member] = member.TickWhere.Z + raise;

            // A round that struck without killing: the character's own flinch, the way it pushed him.
            if (view.LastHit.Count != _modernHitsSeen.GetValueOrDefault(member))
            {
                _modernHitsSeen[member] = view.LastHit.Count;
                if (asset.Hit(view.LastHit.Ahead, view.LastHit.Right) is { } hit)
                {
                    body.Flinch(hit);
                }
            }
        }
        else
        {
            // His own death clip brings where he is drawn down to the floor, and the character's fall its own hips: however far
            // he is drawn lower than he stood is given back, or he would sink twice.
            raise = (float)(_modernFallHeights.GetValueOrDefault(member, member.TickWhere.Z) - member.TickWhere.Z);
        }

        double dx = member.TickWhere.X - member.PrevTickWhere.X, dy = member.TickWhere.Y - member.PrevTickWhere.Y;
        float moved = member.HasTickState ? (float)Math.Sqrt((dx * dx) + (dy * dy)) : 0f;
        // In a fight his rifle comes up, and his shots fire it.
        bool aiming = _sim.Brains.TryGetValue(view, out Igi1HumanAi? fighter)
            && (fighter.HasCombatRecord || fighter.Activity is AiSoldierActivity.Engaging or AiSoldierActivity.FiringAtTask);
        body.Tick(moved, GameplayCVars.ModernBodyYaw.Value * MathF.PI / 180f, raise, aiming, member.Agent.ShotSequence);
    }

    private readonly Dictionary<SoldierMember, string> _modernForcedSeen = [];
    private int _modernForcedTicks;

    // A test range's pose (dev_modern_pose), whatever the soldier is doing: the clips played on the spot at their own pace.
    private void TickForcedPose(SoldierMember member, ModernBody body, ModernBodyAsset asset, string forced)
    {
        bool fresh = _modernForcedSeen.GetValueOrDefault(member) != forced;
        _modernForcedSeen[member] = forced;
        float yaw = GameplayCVars.ModernBodyYaw.Value * MathF.PI / 180f;
        if (forced.StartsWith("die", StringComparison.Ordinal))
        {
            if (fresh || !body.Fallen)
            {
                body.Fall(asset.Death(forced.Contains("back", StringComparison.Ordinal), forced.EndsWith("head", StringComparison.Ordinal))!);
            }

            body.Tick(0f, yaw);
            return;
        }

        if (fresh && forced.StartsWith("hit_", StringComparison.Ordinal))
        {
            (float ahead, float right) = forced switch
            {
                "hit_back" => (-1f, 0f),
                "hit_left" => (0f, -1f),
                "hit_right" => (0f, 1f),
                _ => (1f, 0f),
            };
            if (asset.Hit(ahead, right) is { } hit)
            {
                body.Flinch(hit);
            }
        }

        const float tick = 1f / 30f;
        float moved = forced switch
        {
            "walk" => asset.Walk.MetresPerSecond * tick,
            "aim_walk" => asset.AimWalk.MetresPerSecond * tick,
            "run" => asset.Run.MetresPerSecond * tick,
            _ => 0f,
        };
        bool aiming = forced is "aim" or "fire" or "aim_walk";
        int shots = forced == "fire" ? ++_modernForcedTicks / 10 : 0;
        body.Tick(moved, yaw, 0f, aiming, shots);
    }

    // The fall the killing shot calls for, started on the body he died in; false where there is none to play.
    private bool StartFall(SoldierMember member, ModernBodyAsset asset, SoldierView view)
    {
        if (member.Modern is not { } body || asset.Death(view.KillShot.FromBehind, view.KillShot.Headshot) is not { } fall)
        {
            return false;
        }

        // The height he was drawn at standing, from the tick before: this tick's may already be his death clip's.
        _modernFallHeights[member] = _modernStandingHeights.GetValueOrDefault(member, member.TickWhere.Z);
        body.Fall(fall);
        return true;
    }

    // The character, read the first time it is wanted and again whenever the file named changes; null while off or unreadable.
    private ModernBodyAsset? ModernAsset()
    {
        if (!GameplayCVars.ModernBodies.Value)
        {
            return null;
        }

        string path = GameplayCVars.ModernBodyFile.Value;
        if (path != _modernAssetPath)
        {
            ReleaseModernBodies();
            _modernAssetPath = path;
            _modernAssetFailed = false;
        }

        if (_modernAsset is null && !_modernAssetFailed)
        {
            try
            {
                _modernAsset = ModernBodyAsset.Load(_device, path);
                _logger.Information(LogCategory, FormattableString.Invariant(
                    $"Modern bodies from {path}: {_modernAsset.Vertices.Length} vertices, {_modernAsset.Indices.Length / 3} triangles, {_modernAsset.Joints.Length} joints, walk {_modernAsset.WalkMetresPerSecond:0.00} m/s."));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException
                or NotSupportedException or SharpGLTF.Validation.DataException)
            {
                // Once: a soldier a tick asking again would fill the log, and the original models carry on meanwhile.
                _modernAssetFailed = true;
                _logger.Warning(LogCategory, $"Modern bodies off: could not read '{path}': {ex.Message}");
            }
        }

        if (_modernAsset is not null)
        {
            // The vertex colour is decoded from sRGB before it multiplies the albedo, so the linear tone is encoded into it.
            float tone = GameplayCVars.ModernBodyTone.Value;
            byte grey = (byte)Math.Round(255.0 * (tone <= 0.0031308f ? 12.92 * tone : (1.055 * Math.Pow(tone, 1.0 / 2.4)) - 0.055));
            _modernAsset.Tone = Bgra32.FromRgba(grey, grey, grey, 255);
        }

        return _modernAsset;
    }

    private static void DropModernBody(SoldierMember member)
    {
        member.Modern?.Dispose();
        member.Modern = null;
    }

    // Every soldier's modern body, and the character they were made from.
    private void ReleaseModernBodies()
    {
        foreach (SoldierGroup group in _groups)
        {
            foreach (SoldierMember member in group.Soldiers)
            {
                DropModernBody(member);
            }
        }

        _modernAsset?.Dispose();
        _modernAsset = null;
    }
}
