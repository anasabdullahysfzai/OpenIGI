using Xunit;

namespace OpenIGI.Rendering.Tests;

/// <summary>
/// That the eye opens further under a night sky than under a dim day, and no further than the histogram
/// can measure.
/// </summary>
public sealed class ExposureAdaptationNightTests
{
    [Fact]
    public void A_dim_day_stops_the_eye_at_ten_times_and_is_shown_darker()
    {
        // The floor is still ten times; a view that dim by day is then held under middle grey rather
        // than lifted to it -- three tenths of a stop for every stop it meters under -- so a shaded room reads
        // darker than an open yard.
        Assert.Equal(10f * MathF.Pow(2f, 0.3f * MathF.Log2(0.1f)), new ExposureAdaptation().Adapt(DarkScene(), 1f / 60f), 3);
    }

    [Fact]
    public void A_full_night_opens_as_far_as_the_setting_says()
    {
        // With the whole frame metered, a night's average sits on this floor, so the floor is the night's exposure.
        float previous = ExposureCVars.NightOpenStops.Value;
        ExposureCVars.NightOpenStops.Value = 0.4f;
        using IDisposable restore = new Restore(() => ExposureCVars.NightOpenStops.Value = previous);
        Assert.Equal(10f * MathF.Pow(2f, 0.4f), new ExposureAdaptation().Adapt(DarkScene(), 1f / 60f, darkAdaptation: 1f), 3);
    }

    [Fact]
    public void A_bright_scene_is_metered_the_same_by_night_as_by_day()
    {
        float day = new ExposureAdaptation().Adapt(Scene(bin: 40), 1f / 60f);
        float night = new ExposureAdaptation().Adapt(Scene(bin: 40), 1f / 60f, darkAdaptation: 1f);

        Assert.Equal(day, night, 5);
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    // Every pixel in the second bucket: 2^-7.8, darker than either floor.
    private static uint[] DarkScene() => Scene(bin: 1);

    private static uint[] Scene(int bin)
    {
        uint[] histogram = new uint[ExposureAdaptation.Bins];
        histogram[bin] = 1_000_000u;
        return histogram;
    }
}
