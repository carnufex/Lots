using System.Collections.Concurrent;

namespace Lots.Shell.Core.Mcp;

/// <summary>
/// Secret references (#62): a profile names where a secret is, never the secret. <c>NAME</c> or <c>env:NAME</c> = an environment
/// variable; <c>file:/path</c> = a file (a mounted Kubernetes Secret, ExternalSecrets from Bitwarden/Vault, a Vault agent sink).
/// Files are read on every use, so rotating the secret needs no restart.
/// </summary>
public static class SecretReference
{
    public static string Resolve(string reference, Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        if (reference.StartsWith("file:", StringComparison.Ordinal))
        {
            var path = reference[5..];
            return File.Exists(path) && File.ReadAllText(path).Trim() is { Length: > 0 } v
                ? v
                : throw new InvalidOperationException($"Secret file '{path}' (referenced by the profile) is missing or empty.");
        }
        var name = reference.StartsWith("env:", StringComparison.Ordinal) ? reference[4..] : reference;
        return env(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Environment variable '{name}' (referenced by the profile) is not set.");
    }

    /// <summary>Where a reference points, for display: never the value.</summary>
    public static string Describe(string reference) =>
        reference.StartsWith("file:", StringComparison.Ordinal) ? $"file {reference[5..]}" : $"env {reference.Replace("env:", "")}";
}

/// <summary>What is known about a server's service credentials: last token fetched, its expiry, last use and the last error.</summary>
public sealed record CredentialStatus(DateTimeOffset? LastFetched, DateTimeOffset? ExpiresAt, DateTimeOffset? LastUsed, string? LastError);

/// <summary>Live status per server for the Credentials tab. Never holds secrets or tokens.</summary>
public sealed class CredentialStatusRegistry
{
    private readonly ConcurrentDictionary<string, CredentialStatus> _status = new();

    public CredentialStatus Get(string server) => _status.GetValueOrDefault(server) ?? new CredentialStatus(null, null, null, null);

    public void Fetched(string server, DateTimeOffset at, DateTimeOffset? expires) =>
        _status.AddOrUpdate(server, _ => new(at, expires, at, null), (_, s) => s with { LastFetched = at, ExpiresAt = expires, LastUsed = at, LastError = null });

    public void Used(string server, DateTimeOffset at) =>
        _status.AddOrUpdate(server, _ => new(null, null, at, null), (_, s) => s with { LastUsed = at });

    public void Failed(string server, DateTimeOffset at, string error) =>
        _status.AddOrUpdate(server, _ => new(null, null, null, error), (_, s) => s with { LastError = $"{at:u}: {error}" });
}
