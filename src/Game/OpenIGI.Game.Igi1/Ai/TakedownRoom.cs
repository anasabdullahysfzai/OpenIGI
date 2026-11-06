using OpenIGI.Core.Numerics;
using OpenIGI.Game.World;

namespace OpenIGI.Game.Igi1.Ai;

/// <summary>
/// Whether a takedown's two bodies fit where a soldier stands: the clip starts the attacker behind him and lays him out ahead, on the
/// level, so a wall, a step or a table in that space would have them through it. <b>Not retail.</b>
/// </summary>
public static class TakedownRoom
{
    /// <summary>The space the pair takes, in metres from the victim: behind him, ahead of him, and to each side.</summary>
    public const double BehindMetres = 1.0, AheadMetres = 1.9, AsideMetres = 0.5;

    /// <summary>The most the ground may be above or below his within that space, in metres.</summary>
    public const double UnevenMetres = 0.25;

    private static readonly double[] Heights = [0.5, 1.2];

    /// <summary>Whether the pair fits.</summary>
    /// <param name="at">Where he stands, in world units.</param>
    /// <param name="facing">His facing; forward is -sin, cos of it.</param>
    /// <param name="walls">What a line meets.</param>
    /// <param name="ground">The height of the ground under a point (x, y, from what height down), or null for none.</param>
    /// <returns>Whether nothing is in the way and the ground is level.</returns>
    public static bool Fits(Vector3d at, float facing, WorldRayCast walls, Func<double, double, double, double?> ground)
    {
        ArgumentNullException.ThrowIfNull(walls);
        ArgumentNullException.ThrowIfNull(ground);
        double metre = WorldScene.UnitsPerMetre;
        Vector3d ahead = new(-Math.Sin(facing) * metre, Math.Cos(facing) * metre, 0.0), right = new(Math.Cos(facing) * metre, Math.Sin(facing) * metre, 0.0);
        double from = at.Z + metre;
        if (ground(at.X, at.Y, from) is not { } floor)
        {
            return false;
        }

        foreach (double along in new[] { -BehindMetres, -BehindMetres / 2.0, 0.0, AheadMetres / 3.0, AheadMetres * 2.0 / 3.0, AheadMetres })
        {
            foreach (double across in new[] { -AsideMetres, 0.0, AsideMetres })
            {
                Vector3d point = at + (ahead * along) + (right * across);
                if (ground(point.X, point.Y, from) is not { } height || Math.Abs(height - floor) > UnevenMetres * metre)
                {
                    return false;
                }

                // Clear from his middle to each edge of the space, at the knee and the chest.
                if (along is not (-BehindMetres or AheadMetres) && across == 0.0)
                {
                    continue;
                }

                foreach (double up in Heights)
                {
                    Vector3d start = new(at.X, at.Y, floor + (up * metre)), end = new(point.X, point.Y, floor + (up * metre));
                    if (walls(start, end, out _))
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }
}
