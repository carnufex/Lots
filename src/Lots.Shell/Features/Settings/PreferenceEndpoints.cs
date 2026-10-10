using FastEndpoints;
using Lots.Shell.Core.Policy;
using Lots.Shell.Core.Profiles;
using Lots.Shell.Persistence;

namespace Lots.Shell.Features.Settings;

/// <param name="Theme">system, light or dark.</param>
/// <param name="Language">The UI language (en, sv); null = the browser's.</param>
/// <param name="DefaultContext">The context (profile) new chats and runs start in; null = the first one.</param>
/// <param name="AutoSpeak">Read final answers aloud automatically.</param>
public sealed record PreferencesDto(string Theme, string? Language, string? DefaultContext, bool AutoSpeak);

/// <summary>Account preferences (#155): what the UI looks like and starts with, stored per user so it follows them across devices.</summary>
public static class Preferences
{
    public static readonly string[] Themes = ["system", "light", "dark"];
    public static readonly string[] Languages = ["en", "sv"];

    public static PreferencesDto ToDto(UserSettingsRecord? s) => new(s?.Theme ?? "system", s?.UiLanguage, s?.DefaultContext, s?.AutoSpeak ?? false);
}

public sealed class GetPreferencesEndpoint(LotsDbContext db, ICurrentPrincipal who) : EndpointWithoutRequest<PreferencesDto>
{
    public override void Configure() => Get("/me/preferences");

    public override async Task HandleAsync(CancellationToken ct) =>
        await Send.OkAsync(Preferences.ToDto(await UserSettings.OfAsync(db, who.Get(HttpContext).UserId, ct)), ct);
}

public sealed class SetPreferencesEndpoint(LotsDbContext db, ICurrentPrincipal who, ProfileRegistry profiles, TimeProvider clock)
    : Endpoint<PreferencesDto, PreferencesDto>
{
    public override void Configure() => Put("/me/preferences");

    public override async Task HandleAsync(PreferencesDto req, CancellationToken ct)
    {
        var me = who.Get(HttpContext);
        if (!Preferences.Themes.Contains(req.Theme)) AddError(x => x.Theme, "Theme is system, light or dark.");
        if (req.Language is { } l && !Preferences.Languages.Contains(l)) AddError(x => x.Language, "Language is en or sv.");
        // Only a context the user can actually use; a default that policy would refuse would only produce failing runs.
        if (req.DefaultContext is { } c && !profiles.All.Any(p => p.Name == c && p.Roles.Any(r => r.Allow.Count > 0 && me.Roles.Contains(r.Name, StringComparer.OrdinalIgnoreCase))))
            AddError(x => x.DefaultContext, $"'{c}' is not a context you can use.");
        ThrowIfAnyErrors();

        var row = await UserSettings.GetOrAddAsync(db, me.UserId, ct);
        row.Theme = req.Theme == "system" ? null : req.Theme;
        row.UiLanguage = req.Language;
        row.DefaultContext = req.DefaultContext;
        row.AutoSpeak = req.AutoSpeak;
        row.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await Send.OkAsync(Preferences.ToDto(row), ct);
    }
}
