using System.Numerics;

namespace OpenIGI.Presentation.Igi1;

/// <summary>
/// The modern body's blend from one clip to the next by inertialization: where the clips change, each node keeps the offset
/// it had from the new clip's pose, which then dies away on a critically damped spring, so the body carries on from where it
/// was instead of the two clips being averaged over a fade (which looked soft and swam when the clips changed often). Only
/// the new clip is evaluated. <b>Not retail.</b>
/// </summary>
internal sealed class ModernInertia(int nodes)
{
    /// <summary>The spring's stiffness, in a second: critically damped, an offset is nine tenths gone in about a third of one.</summary>
    public const float Omega = 12f;

    private readonly Vector3[] _lastT = new Vector3[nodes], _offT = new Vector3[nodes], _velT = new Vector3[nodes];
    private readonly Quaternion[] _lastR = new Quaternion[nodes];
    private readonly Vector3[] _offR = new Vector3[nodes], _velR = new Vector3[nodes];
    private bool _posed;

    /// <summary>Gets or sets whether the next pose begins a new blend: its offsets taken from the pose before.</summary>
    public bool Begin { get; set; }

    /// <summary>Lets every offset die away by a step of time.</summary>
    /// <param name="seconds">The step.</param>
    public void Step(float seconds)
    {
        for (int node = 0; node < _offT.Length; node++)
        {
            Settle(ref _offT[node], ref _velT[node], seconds);
            Settle(ref _offR[node], ref _velR[node], seconds);
        }
    }

    /// <summary>Applies a node's offset to its pose from the clips, taking the offset first where a blend begins.</summary>
    /// <param name="node">The node.</param>
    /// <param name="t">Its translation from the clips, offset on return.</param>
    /// <param name="r">Its rotation from the clips, offset on return.</param>
    public void Apply(int node, ref Vector3 t, ref Quaternion r)
    {
        if (Begin && _posed)
        {
            // Where he was, against where the new clip has him: the offset, at rest.
            _offT[node] = _lastT[node] - t;
            Quaternion off = Quaternion.Normalize(_lastR[node] * Quaternion.Inverse(r));
            _offR[node] = ToVector(off);
            (_velT[node], _velR[node]) = (Vector3.Zero, Vector3.Zero);
        }

        t += _offT[node];
        r = Quaternion.Normalize(FromVector(_offR[node]) * r);
        (_lastT[node], _lastR[node]) = (t, r);
    }

    /// <summary>Marks a pose done: the next can begin a blend from it.</summary>
    public void Posed()
    {
        _posed = true;
        Begin = false;
    }

    // One step of a critically damped spring toward nought.
    private static void Settle(ref Vector3 x, ref Vector3 v, float dt)
    {
        if (x == Vector3.Zero && v == Vector3.Zero)
        {
            return;
        }

        float decay = MathF.Exp(-Omega * dt);
        Vector3 j = v + (Omega * x);
        x = (x + (j * dt)) * decay;
        v = (v - (Omega * dt * j)) * decay;
    }

    // A rotation as its axis times its angle, the short way round.
    private static Vector3 ToVector(Quaternion q)
    {
        if (q.W < 0f)
        {
            q = -q;
        }

        Vector3 axis = new(q.X, q.Y, q.Z);
        float sine = axis.Length();
        return sine < 1e-6f ? Vector3.Zero : axis / sine * (2f * MathF.Atan2(sine, q.W));
    }

    private static Quaternion FromVector(Vector3 v)
    {
        float angle = v.Length();
        return angle < 1e-6f ? Quaternion.Identity : Quaternion.CreateFromAxisAngle(v / angle, angle);
    }
}
