using OpenIGI.Core.Diagnostics;
using OpenIGI.Game;

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

    /// <summary>Gives a soldier his modern body for this tick, or takes it away.</summary>
    /// <param name="group">The soldier's group, whose model stands him on the ground.</param>
    /// <param name="member">The soldier.</param>
    private void TickModernBody(SoldierGroup group, SoldierMember member)
    {
        if (member.Agent.View.IsDead || ModernAsset() is not { } asset)
        {
            DropModernBody(member);
            return;
        }

        if (member.Modern is null)
        {
            // His feet where his own model's are: its lowest vertex, in metres, which sets the body on the ground. (Not his
            // skin scratch: until his first skinning succeeds that holds the model's raw geometry, in its own units.)
            member.Modern = new ModernBody(asset, _device);
            member.Modern.MatchLowest(group.Body.LowestVertexZ);
        }

        ModernBody body = member.Modern;

        double dx = member.TickWhere.X - member.PrevTickWhere.X, dy = member.TickWhere.Y - member.PrevTickWhere.Y;
        float moved = member.HasTickState ? (float)Math.Sqrt((dx * dx) + (dy * dy)) : 0f;
        body.Tick(moved, GameplayCVars.ModernBodyYaw.Value * MathF.PI / 180f);
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
