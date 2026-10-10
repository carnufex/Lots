using Lots.Shell.Core.Policy;

namespace Lots.Shell.Core.Security;

/// <summary>
/// Settings that are fine on a laptop and dangerous anywhere else (#87). Outside the Development environment the shell refuses to
/// start with them, unless <c>Security:AllowInsecureSettings</c> is set, which is logged as a warning on every start.
/// </summary>
public static class SecurityChecks
{
    public static IReadOnlyList<string> Problems(IConfiguration config)
    {
        var problems = new List<string>();
        if (string.Equals(config["Auth:Mode"], "dev", StringComparison.OrdinalIgnoreCase))
            problems.Add("Auth:Mode is Dev: every request is authenticated as the configured dev user");
        if (config.GetValue("Auth:Dev:AllowHeaders", false))
            problems.Add("Auth:Dev:AllowHeaders is on: any caller can pick an identity with X-Dev-User/X-Dev-Roles");
        if (AuthSetup.IsOidc(config) && Uri.TryCreate(config["Auth:Oidc:Authority"], UriKind.Absolute, out var authority)
            && authority.Scheme != "https" && !authority.IsLoopback)
            problems.Add($"Auth:Oidc:Authority {authority} is not https: tokens and keys would travel unencrypted");
        return problems;
    }

    /// <summary>Throws when insecure settings are used outside Development; returns warnings to log otherwise.</summary>
    public static IReadOnlyList<string> Enforce(IConfiguration config, IHostEnvironment env)
    {
        var problems = Problems(config);
        if (problems.Count == 0 || env.IsDevelopment()) return problems;
        if (config.GetValue("Security:AllowInsecureSettings", false))
            return problems.Select(p => $"{p} (allowed by Security:AllowInsecureSettings in {env.EnvironmentName})").ToList();
        throw new InvalidOperationException(
            $"Refusing to start in the {env.EnvironmentName} environment with insecure settings: {string.Join("; ", problems)}. " +
            "Use Auth:Mode=Oidc, run with ASPNETCORE_ENVIRONMENT=Development for local work, or set Security:AllowInsecureSettings=true " +
            "for a throwaway demo.");
    }

    /// <summary>Wraps every logging provider so messages pass through <see cref="SecretRedactor"/> before they are written.</summary>
    public static void AddSecretRedaction(this ILoggingBuilder logging)
    {
        var providers = logging.Services.Where(d => d.ServiceType == typeof(ILoggerProvider)).ToList();
        foreach (var d in providers)
        {
            logging.Services.Remove(d);
            logging.Services.Add(ServiceDescriptor.Singleton<ILoggerProvider>(sp => new RedactingLoggerProvider(Create(sp, d))));
        }
    }

    private static ILoggerProvider Create(IServiceProvider sp, ServiceDescriptor d) =>
        (ILoggerProvider)(d.ImplementationInstance ?? d.ImplementationFactory?.Invoke(sp) ?? ActivatorUtilities.CreateInstance(sp, d.ImplementationType!));
}

public sealed class RedactingLoggerProvider(ILoggerProvider inner) : ILoggerProvider, ISupportExternalScope
{
    public ILogger CreateLogger(string categoryName) => new RedactingLogger(inner.CreateLogger(categoryName));

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => (inner as ISupportExternalScope)?.SetScopeProvider(scopeProvider);

    public void Dispose() => inner.Dispose();

    private sealed class RedactingLogger(ILogger inner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            inner.Log(logLevel, eventId, state, exception is null ? null : new RedactedException(exception),
                (s, e) => SecretRedactor.Redact(formatter(s, exception)));
    }

    /// <summary>The exception as text with secrets masked (log sinks print ToString()).</summary>
    private sealed class RedactedException(Exception inner) : Exception(SecretRedactor.Redact(inner.Message))
    {
        public override string ToString() => SecretRedactor.Redact(inner.ToString());
        public override string? StackTrace => null;
    }
}
