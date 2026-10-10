using Lots.Shell.Core.Profiles;
using Lots.Shell.Core.Telemetry;

namespace Lots.Shell.Tests.Features;

/// <summary>#145: the content mode is per deployment and per profile, and full content needs the deployment's explicit consent.</summary>
public class TelemetryPrivacyTests
{
    private const string Yaml = "name: p\nversion: 1\ntelemetry:\n  content: {0}\n";

    [Fact]
    public void A_profile_sets_its_own_mode_and_bad_values_are_refused()
    {
        Assert.Equal(ContentCapture.Redacted, ProfileParser.Parse(string.Format(Yaml, "redacted")).TelemetryContent);
        Assert.Null(ProfileParser.Parse("name: p\nversion: 1\n").TelemetryContent);
        Assert.Throws<ProfileException>(() => ProfileParser.Parse(string.Format(Yaml, "everything")));
        Assert.Throws<ProfileException>(() => ProfileParser.Parse(string.Format(Yaml, "2")));
    }

    [Fact]
    public void Full_content_is_clamped_to_redacted_unless_the_deployment_allows_it()
    {
        var profile = ProfileParser.Parse(string.Format(Yaml, "full"));
        var allowed = Tracing.AllowFullContent;
        try
        {
            Tracing.AllowFullContent = false;
            Assert.Equal(ContentCapture.Redacted, Tracing.ContentFor(profile));
            Tracing.AllowFullContent = true;
            Assert.Equal(ContentCapture.Full, Tracing.ContentFor(profile));
        }
        finally
        {
            Tracing.AllowFullContent = allowed;
        }
    }

    [Fact]
    public void Without_a_profile_setting_the_deployment_default_applies()
    {
        Assert.Equal(Tracing.DefaultContent, Tracing.ContentFor(ProfileParser.Parse("name: p\nversion: 1\n")));
    }
}
