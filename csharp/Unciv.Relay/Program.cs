using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: InternalsVisibleTo("Unciv.App.Tests")]

namespace Unciv.Relay;

/// <summary>T337: the relay executable. <c>Unciv.Relay --help</c> lists the options; the work is in <see cref="RelayRunner"/> and <see cref="RelayServer"/>.</summary>
public static class Program
{
    public static int Main(string[] args) =>
        RunWithStreams(args, Console.Out, Console.Error);

    internal static int RunWithStreams(string[] args, TextWriter stdout, TextWriter stderr)
    {
        using var stop = new CancellationTokenSource();
        var reload = new ReloadSignal();
        // SIGINT and SIGTERM stop gracefully (the first one; a second falls through to the runtime's default so a stuck drain can be ended); SIGHUP reloads certificates and policy files.
        using var sigint = TryRegister(PosixSignal.SIGINT, context => HandleStop(context, stop));
        using var sigterm = TryRegister(PosixSignal.SIGTERM, context => HandleStop(context, stop));
        using var sighup = TryRegister(PosixSignal.SIGHUP, context => HandleReload(context, reload));
        return RelayRunner.RunAsync(args, stdout, stderr, stop.Token, reload).GetAwaiter().GetResult();
    }

    internal static void HandleStop(PosixSignalContext context, CancellationTokenSource stop)
    {
        context.Cancel = true;
        try { stop.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    internal static void HandleReload(PosixSignalContext context, ReloadSignal reload)
    {
        context.Cancel = true;
        reload.Request();
    }

    private static PosixSignalRegistration? TryRegister(PosixSignal signal, Action<PosixSignalContext> handler)
    {
        try { return PosixSignalRegistration.Create(signal, handler); }
        catch (PlatformNotSupportedException) { return null; }
    }
}
