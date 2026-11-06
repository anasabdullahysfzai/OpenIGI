using OpenIGI.Audio;
using OpenIGI.Core.Numerics;
using OpenIGI.Engine.Audio;

namespace OpenIGI.Game.Igi1.World;

/// <content>
/// The modern AI's spoken callouts (<see cref="GameplayCVars.CalloutDirectory"/>): plain 16-bit WAV files from a folder, read when first
/// wanted. Not retail, which has only its six detection shouts; with no folder named, guards simply say nothing new.
/// </content>
public sealed partial class Igi1MissionAudio
{
    /// <summary>How far a callout carries before it fades out, in metres.</summary>
    public const float CalloutReachMetres = 45f;

    private const string CalloutPrefix = "callout_";

    private readonly HashSet<string> _calloutsTried = [];

    // Plays a callout if this is one, from the folder; true if the name was a callout, played or not.
    private bool TryPlayCallout(SoundSystem sounds, string name, Vector3d position)
    {
        if (!name.StartsWith(CalloutPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        if (_calloutsTried.Add(name))
        {
            string path = Path.Combine(GameplayCVars.CalloutDirectory.Value, name + ".wav");
            if (File.Exists(path) && ReadWave(File.ReadAllBytes(path)) is var (format, pcm))
            {
                sounds.RegisterExternal(name, format, pcm);
            }
        }

        sounds.PlayExternal(name, position, gain: 1f, ScriptSoundPriority, maximumDistanceInMetres: CalloutReachMetres);
        return true;
    }

    /// <summary>Plays one sound of a knife takedown: takedown_{cue}.wav from the callouts' folder where there is one, else one of the game's own.</summary>
    /// <param name="cue">Which: grab, struggle, stab, out, gasp or fall.</param>
    /// <param name="fallback">The game's sound.</param>
    /// <param name="position">Where, in world units.</param>
    public void PlayTakedownCue(string cue, string fallback, Vector3d position)
    {
        if (host.SoundOutput is not { } sounds)
        {
            return;
        }

        string name = "takedown_" + cue;
        string path = Path.Combine(GameplayCVars.CalloutDirectory.Value, name + ".wav");
        if (File.Exists(path))
        {
            if (_calloutsTried.Add(name) && ReadWave(File.ReadAllBytes(path)) is var (format, pcm))
            {
                sounds.RegisterExternal(name, format, pcm);
            }

            sounds.PlayExternal(name, position, gain: 1f, ScriptSoundPriority, maximumDistanceInMetres: CalloutReachMetres);
            return;
        }

        sounds.PlayDefinition(fallback, position, priority: ScriptSoundPriority);
    }

    /// <summary>Reads a plain PCM WAV.</summary>
    /// <param name="file">The whole file.</param>
    /// <returns>Its format and samples; null for anything else.</returns>
    public static (AudioFormat Format, byte[] Pcm)? ReadWave(byte[] file)
    {
        AudioFormat? format = null;
        for (int at = 12; at + 8 <= file.Length;)
        {
            int size = BitConverter.ToInt32(file, at + 4);
            string chunk = System.Text.Encoding.ASCII.GetString(file, at, 4);
            if (chunk == "fmt ")
            {
                format = new AudioFormat(BitConverter.ToInt32(file, at + 12), BitConverter.ToInt16(file, at + 10), BitConverter.ToInt16(file, at + 22));
            }
            else if (chunk == "data" && format is { } known)
            {
                return (known, file[(at + 8)..Math.Min(file.Length, at + 8 + size)]);
            }

            at += 8 + size + (size & 1);
        }

        return null;
    }
}
