namespace Unciv.Utils;

// T261: a measurement seam, not part of the Kotlin port. The engine never reads a clock (BannedSymbols forbids Stopwatch here): it only tells an
// optional observer when a named phase begins and ends, and the observer (the CLI bench) does the timing. Without an observer a scope costs one
// thread-static read and allocates nothing. The probe never reads or writes game state, so state hashes are identical with and without it.

/// <summary>Receives the begin and end of the engine's named phases, strictly nested, for the <see cref="Logic.GameInfo"/> it is attached to.</summary>
public interface ITurnProbe
{
    void Enter(string phase, string? subject);

    void Exit(string phase, string? subject);
}

public static class TurnProbe
{
    /// <summary>Reports <paramref name="phase"/> (and its <paramref name="subject"/>, usually a civ id) to <paramref name="probe"/> until the result is disposed.</summary>
    public static Scope Measure(ITurnProbe? probe, string phase, string? subject = null)
    {
        probe?.Enter(phase, subject);
        return new Scope(probe, phase, subject);
    }

    public readonly struct Scope(ITurnProbe? probe, string phase, string? subject) : IDisposable
    {
        public void Dispose() => probe?.Exit(phase, subject);
    }
}
