using System.Security.Cryptography;

namespace Unciv.Net.Identity;

/// <summary>OS CSPRNG implementation for host and network boundaries, outside Unciv.Core.</summary>
public sealed class SystemSecureRandom : ISecureRandom
{
    public static readonly SystemSecureRandom Instance = new();

    public void Fill(Span<byte> buffer) => RandomNumberGenerator.Fill(buffer);
}
