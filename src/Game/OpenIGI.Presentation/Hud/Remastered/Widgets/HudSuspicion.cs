using OpenIGI.Core.Graphics;
using OpenIGI.UI.Layout;

namespace OpenIGI.Presentation.Hud.Remastered.Widgets;

/// <summary>
/// How far each soldier is to seeing the player, in a modern stealth game's manner: for every soldier noticing him, a meter on a
/// ring around the middle of the screen, on the soldier's side. A long dark track tapering to a point at each end, in an orange
/// glow, with a nub on its outer side pointing out towards the soldier; a thick white bar grows out from its middle towards both
/// ends as the soldier makes the player out, and the whole turns red and pulses once he sees him. <b>Not retail</b>: the modern
/// AI's meter.
/// </summary>
/// <remarks>
/// It reads the threats' awareness as the soldiers hand it over -- nought to a half while suspicion fills, a half to one once
/// seen -- so a meter is full the moment its soldier sees the player.
/// </remarks>
public static class HudSuspicion
{
    private static readonly Bgra32 Bar = Bgra32.FromRgba(255, 252, 245, 255);
    private static readonly Bgra32 Track = Bgra32.FromRgba(45, 45, 48, 255);
    private static readonly Bgra32 Glow = Bgra32.FromRgba(255, 120, 20, 255);

    /// <summary>The meter's length either side of the soldier's bearing, in radians.</summary>
    private const float HalfSpan = 0.42f;

    /// <summary>How many pieces the tapering track is drawn in.</summary>
    private const int Slices = 24;

    /// <summary>Draws the meters.</summary>
    /// <param name="context">The frame's drawing.</param>
    /// <param name="model">The frame's model.</param>
    public static void Draw(HudDrawContext context, HudModel model)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(model);
        UiVec2 centre = new(context.Width * 0.5f, context.Height * 0.5f);
        float radius = context.Px(150f);
        float appear = context.Reactive.Appear;
        float pulse = 0.7f + (0.3f * MathF.Sin(context.Seconds * 12f));

        foreach (HudThreat threat in model.Threats)
        {
            bool seen = threat.Awareness >= 0.5f;
            float fill = MathF.Min(1f, threat.Awareness * 2f);
            float strength = appear * (seen ? pulse : 1f);
            Bgra32 glow = HudDrawContext.Fade(seen ? context.Palette.Danger : Glow, 0.4f * strength);
            Bgra32 track = HudDrawContext.Fade(Track, 0.85f * appear);
            Bgra32 bar = HudDrawContext.Fade(seen ? context.Palette.Danger : Bar, strength);

            // The glow, then the track over it: both taper to a point at each end.
            Tapered(context, centre, radius, threat.Bearing, context.Px(11f), glow);
            Tapered(context, centre, radius, threat.Bearing, context.Px(3.5f), track);

            // The nub on the outer side, pointing out towards the soldier.
            Nub(context, centre, radius + context.Px(2f), threat.Bearing, context.Px(7f), context.Px(5f), bar);

            // The bar, growing out from the middle towards both ends.
            if (fill > 0f)
            {
                float half = HalfSpan * 0.8f * fill;
                float thick = context.Px(6f);
                context.Painter.FillArc(centre, radius - (thick * 0.5f), radius + (thick * 0.5f), threat.Bearing - half, threat.Bearing + half, bar);
            }
        }
    }

    // A band along the ring, its thickness falling from the middle to nothing at the ends.
    private static void Tapered(HudDrawContext context, UiVec2 centre, float radius, float bearing, float thickest, Bgra32 colour)
    {
        float step = (HalfSpan * 2f) / Slices;
        for (int i = 0; i < Slices; i++)
        {
            float from = bearing - HalfSpan + (i * step);
            float t = MathF.Abs(((i + 0.5f) / Slices * 2f) - 1f);
            float thick = thickest * MathF.Max(0.08f, 1f - (t * t));
            context.Painter.FillArc(centre, radius - (thick * 0.5f), radius + (thick * 0.5f), from, from + step, colour);
        }
    }

    // A small triangle on the ring's outer side at the bearing, its point away from the centre.
    private static void Nub(HudDrawContext context, UiVec2 centre, float radius, float bearing, float width, float depth, Bgra32 colour)
    {
        float across = width * 0.5f / radius;
        context.Painter.FillTriangle(
            OnRing(centre, radius, bearing - across),
            OnRing(centre, radius + depth, bearing),
            OnRing(centre, radius, bearing + across),
            colour);
    }

    // A point on a ring, at an angle clockwise from straight up -- the painter's own arc convention.
    private static UiVec2 OnRing(UiVec2 centre, float radius, float angle) =>
        new(centre.X + (radius * MathF.Sin(angle)), centre.Y - (radius * MathF.Cos(angle)));
}
