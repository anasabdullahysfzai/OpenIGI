using OpenIGI.Core.Graphics;
using OpenIGI.UI;
using OpenIGI.UI.Layout;

namespace OpenIGI.Presentation.Hud.Remastered.Widgets;

/// <summary>
/// Over each soldier noticing the player, a small ring that fills as he makes the player out: amber while he is not yet sure,
/// the theme's danger once he sees him. <b>Not retail</b>: the modern AI's meter.
/// </summary>
/// <remarks>
/// It reads the threats' awareness as the soldiers hand it over -- nought to a half while suspicion fills, a half to one once
/// seen -- so the ring is full the moment a soldier sees the player. Soldiers behind the view are left undrawn; the alert
/// banner already says someone is onto him.
/// </remarks>
public static class HudSuspicion
{
    private static readonly Bgra32 Amber = Bgra32.FromRgba(255, 170, 60, 255);

    /// <summary>Draws the rings.</summary>
    /// <param name="context">The frame's drawing.</param>
    /// <param name="model">The frame's model.</param>
    /// <param name="projector">The player's view.</param>
    public static void Draw(HudDrawContext context, HudModel model, HudProjector projector)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(model);
        float outer = context.Px(10f), inner = context.Px(7f);
        foreach (HudThreat threat in model.Threats)
        {
            if (threat.Mark is not { } mark || !projector.TryProject(mark, out UiVec2 at)
                || at.X < 0f || at.Y < 0f || at.X > context.Width || at.Y > context.Height)
            {
                continue;
            }

            bool seen = threat.Awareness >= 0.5f;
            float fill = MathF.Min(1f, threat.Awareness * 2f);
            Bgra32 colour = HudDrawContext.Fade(seen ? context.Palette.Danger : Amber, context.Reactive.Appear);
            context.Painter.FillArc(at, inner, outer, 0f, MathF.Tau, HudDrawContext.Fade(Bgra32.FromRgba(0, 0, 0, 255), 0.45f * context.Reactive.Appear));
            context.Painter.FillArc(at, inner, outer, 0f, MathF.Tau * fill, colour);
            if (seen)
            {
                context.Painter.FillDot(at, context.Px(3f), colour);
            }
        }
    }
}
