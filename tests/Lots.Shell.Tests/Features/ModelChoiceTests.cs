using Lots.Shell.Core.Models;
using Lots.Shell.Core.Policy;
using Lots.Shell.Features.Runs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Lots.Shell.Tests.Features;

/// <summary>Who may pick a run's model and effort (#119), and that only configured aliases can be picked.</summary>
public class ModelChoiceTests
{
    private static readonly ModelCatalog Catalog = new(Options.Create(new ModelsOptions
    {
        Endpoints = { ["local"] = new() { BaseUrl = "http://ollama:11434/v1" } },
        Aliases =
        {
            ["default"] = new() { Targets = [new() { Endpoint = "local", Model = "qwen3.5" }] },
            ["small"] = new() { Targets = [new() { Endpoint = "local", Model = "llama3.2" }] },
        },
    }), Options.Create(new ModelOptions()));

    private static IConfiguration Config(params string[] chooseRoles) => new ConfigurationBuilder().AddInMemoryCollection(
        chooseRoles.Select((r, i) => new KeyValuePair<string, string?>($"Models:ChooseRoles:{i}", r))).Build();

    private static Principal As(params string[] roles) => new("u", roles);

    [Fact]
    public void No_choice_needs_no_role()
    {
        Assert.Null(ModelChoice.Check(null, " ", As(), Config(), Catalog));
    }

    [Fact]
    public void Operators_may_not_choose_by_default()
    {
        var p = ModelChoice.Check("small", null, As("operator"), Config(), Catalog);

        Assert.Equal(403, p!.Status);
        Assert.Equal(403, ModelChoice.Check(null, "high", As("operator"), Config(), Catalog)!.Status);
    }

    [Fact]
    public void Admins_and_configured_roles_may_choose_a_configured_alias()
    {
        Assert.Null(ModelChoice.Check("small", "low", As("admin"), Config(), Catalog));
        Assert.Null(ModelChoice.Check("SMALL", null, As("evaluator"), Config(), Catalog));
        Assert.Null(ModelChoice.Check("small", null, As("tester"), Config("tester"), Catalog));
        Assert.Equal(403, ModelChoice.Check("small", null, As("admin"), Config("tester"), Catalog)!.Status);
    }

    [Theory]
    [InlineData("gpt-4o", null)]          // a raw model name is not an alias
    [InlineData("http://evil/v1", null)]  // nor an endpoint
    [InlineData("small", "max")]
    public void Unknown_aliases_and_efforts_are_rejected(string model, string? effort)
    {
        Assert.Equal(400, ModelChoice.Check(model, effort, As("admin"), Config(), Catalog)!.Status);
    }
}
