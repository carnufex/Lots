namespace Lots.Shell.Core.Security;

/// <summary>
/// Browser hardening headers on every response (#88). The content security policy allows only the shell's own scripts and the
/// identity provider the browser talks to for login; nothing can frame the UI. Inline styles stay allowed (React style attributes),
/// inline scripts do not. Audio and downloads use blob: URLs; the microphone worklet is loaded from one.
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next, IConfiguration config, IHostEnvironment env)
{
    private readonly string _csp = BuildCsp(config);
    private readonly bool _hsts = config.GetValue("Security:Hsts", !env.IsDevelopment());

    public static string BuildCsp(IConfiguration config)
    {
        var idp = Uri.TryCreate(config["Auth:Oidc:Authority"], UriKind.Absolute, out var a) ? " " + a.GetLeftPart(UriPartial.Authority) : "";
        var extraConnect = config["Security:CspConnectSrc"] is { Length: > 0 } extra ? " " + extra : "";
        return string.Join("; ",
            "default-src 'self'",
            "script-src 'self' blob:",
            "style-src 'self' 'unsafe-inline'",
            "img-src 'self' data: blob:",
            "media-src 'self' blob:",
            "font-src 'self'",
            $"connect-src 'self'{idp}{extraConnect}",
            $"frame-src{(idp.Length > 0 ? idp : " 'none'")}",
            "frame-ancestors 'none'",
            $"form-action 'self'{idp}",
            "base-uri 'self'",
            "object-src 'none'");
    }

    public Task InvokeAsync(HttpContext http)
    {
        var h = http.Response.Headers;
        h["Content-Security-Policy"] = _csp;
        h["X-Content-Type-Options"] = "nosniff";
        h["X-Frame-Options"] = "DENY";
        h["Referrer-Policy"] = "no-referrer";
        h["Cross-Origin-Opener-Policy"] = "same-origin";
        h["Permissions-Policy"] = "microphone=(self), camera=(), geolocation=(), payment=(), usb=()";
        if (_hsts) h["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
        // API responses carry personal data: no shared caches. Static files set their own Cache-Control later.
        if (!http.Request.Path.StartsWithSegments("/assets")) h.CacheControl = "no-store";
        return next(http);
    }
}
