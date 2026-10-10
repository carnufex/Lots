using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Lots.Shell.Core.Policy;

/// <summary>
/// Role preview tokens (#156): an admin asks for one, the browser sends it as <c>X-Lots-Preview</c>, and while it is valid the admin's
/// requests carry the previewed roles. Signed and encrypted with Data Protection, bound to the user, time-limited, and only honoured
/// for someone who is an admin by their real roles.
/// </summary>
public sealed class PreviewTokens(IDataProtectionProvider protection, TimeProvider clock)
{
    private readonly IDataProtector _protector = protection.CreateProtector("lots.role-preview.v1");

    private sealed record Payload(string User, List<string> Roles, bool AllowWrites, DateTimeOffset Expires);

    public const int MaxMinutes = 60;

    public (string Token, DateTimeOffset Expires) Issue(Principal real, IReadOnlyList<string> roles, bool allowWrites, int minutes)
    {
        var expires = clock.GetUtcNow().AddMinutes(Math.Clamp(minutes, 1, MaxMinutes));
        var json = JsonSerializer.Serialize(new Payload(real.UserId, roles.ToList(), allowWrites, expires));
        return (_protector.Protect(json), expires);
    }

    /// <summary>The previewed principal, or null when the token is invalid, expired, someone else's, or the caller is no admin.</summary>
    public Principal? Read(string token, Principal real, IConfiguration config)
    {
        Payload? p;
        try { p = JsonSerializer.Deserialize<Payload>(_protector.Unprotect(token)); }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException) { return null; }
        if (p is null || p.User != real.UserId || p.Expires <= clock.GetUtcNow() || !IsAdmin(real, config)) return null;
        return new Principal(real.UserId, p.Roles) { Preview = new PreviewInfo(real.Roles, p.AllowWrites, p.Expires) };
    }

    public static bool IsAdmin(Principal p, IConfiguration config)
    {
        var admins = (config["Auth:AdminRoles"] ?? "admin").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return p.Roles.Any(r => admins.Contains(r, StringComparer.OrdinalIgnoreCase));
    }
}
