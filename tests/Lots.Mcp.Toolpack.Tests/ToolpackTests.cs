using System.Net;
using System.Text.Json;
using Lots.Mcp.Toolpack;

namespace Lots.Mcp.Toolpack.Tests;

public class EgressTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.20.0.1")]
    [InlineData("192.168.1.215")]
    [InlineData("169.254.169.254")] // cloud metadata
    [InlineData("100.64.0.1")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fd00::1")]
    [InlineData("::ffff:10.0.0.1")]
    public void Internal_addresses_are_blocked(string ip) => Assert.True(EgressPolicy.IsInternal(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("172.32.0.1")]
    [InlineData("2606:4700:4700::1111")]
    public void Public_addresses_pass(string ip) => Assert.False(EgressPolicy.IsInternal(IPAddress.Parse(ip)));

    [Fact]
    public void Only_allowlisted_hosts_schemes_and_ports_pass()
    {
        var p = new EgressPolicy(["docs.example.com", "*.wiki.example.org"]);
        p.CheckUri(new Uri("https://docs.example.com/a"));
        p.CheckUri(new Uri("https://team.wiki.example.org/page"));
        Assert.Throws<EgressDeniedException>(() => p.CheckUri(new Uri("https://evil.com/")));
        Assert.Throws<EgressDeniedException>(() => p.CheckUri(new Uri("https://docs.example.com.evil.com/")));
        Assert.Throws<EgressDeniedException>(() => p.CheckUri(new Uri("ftp://docs.example.com/")));
        Assert.Throws<EgressDeniedException>(() => p.CheckUri(new Uri("https://docs.example.com:6379/")));
        Assert.Throws<EgressDeniedException>(() => new EgressPolicy(["*"]).CheckUri(new Uri("http://169.254.169.254/latest/meta-data")));
    }

    [Fact]
    public async Task A_host_that_resolves_to_an_internal_address_is_refused_at_connect_time()
    {
        var tools = new FetchTools(new EgressPolicy(["localhost"]), new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        Assert.Contains("internal address", await tools.FetchUrl("http://localhost:8080/"));
    }
}

public class FileSandboxTests
{
    [Fact]
    public void Paths_cannot_leave_the_root_and_hidden_files_stay_hidden()
    {
        var root = Directory.CreateTempSubdirectory("lots-files-");
        var outside = Directory.CreateTempSubdirectory("lots-outside-");
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "runbook.md"), "restart with kubectl");
            File.WriteAllText(Path.Combine(root.FullName, ".env"), "SECRET=1");
            File.WriteAllText(Path.Combine(outside.FullName, "secret.txt"), "nope");
            var tools = new FileTools(new FileSandbox(root.FullName));

            Assert.Equal("restart with kubectl", tools.ReadFile("runbook.md"));
            Assert.Contains("outside", tools.ReadFile("../" + outside.Name + "/secret.txt"));
            Assert.Contains("outside", tools.ReadFile(Path.Combine(outside.FullName, "secret.txt")));
            Assert.Contains("hidden", tools.ReadFile(".env"));
            Assert.DoesNotContain(".env", tools.ListFiles());

            // A link inside the root that points outside it is refused (needs symlink rights on Windows).
            var link = Path.Combine(root.FullName, "escape");
            try { Directory.CreateSymbolicLink(link, outside.FullName); }
            catch (IOException) { return; }
            catch (UnauthorizedAccessException) { return; }
            Assert.Contains("outside", tools.ReadFile("escape/secret.txt"));
        }
        finally
        {
            root.Delete(true);
            outside.Delete(true);
        }
    }

    [Fact]
    public void Long_files_come_in_parts()
    {
        var root = Directory.CreateTempSubdirectory("lots-files-");
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "big.txt"), new string('x', 120));
            var part = new FileTools(new FileSandbox(root.FullName)).ReadFile("big.txt", maxChars: 50);
            Assert.EndsWith("[continues: offset=50 of 120]", part);
        }
        finally
        {
            root.Delete(true);
        }
    }
}

public class SqlGuardTests
{
    [Theory]
    [InlineData("SELECT * FROM hosts")]
    [InlineData("  with x as (select 1) select * from x;")]
    [InlineData("EXPLAIN SELECT 1")]
    [InlineData("select 'drop table; delete' as text")] // keywords inside strings are data
    public void Reads_are_accepted(string sql) => Assert.Null(SqlGuard.Reject(sql));

    [Theory]
    [InlineData("DELETE FROM hosts")]
    [InlineData("select 1; drop table hosts")]
    [InlineData("WITH d AS (DELETE FROM hosts RETURNING *) SELECT * FROM d")]
    [InlineData("select 1 /* ; */ ; update hosts set x = 1")]
    [InlineData("COPY hosts TO '/tmp/x'")]
    [InlineData("select set_config('x','y',false); set role admin")]
    [InlineData("")]
    public void Writes_and_multiple_statements_are_refused(string sql) => Assert.NotNull(SqlGuard.Reject(sql));
}

public class OpenApiTests
{
    private const string Spec = """
        openapi: 3.0.0
        info: { title: CRM, version: "1" }
        paths:
          /customers/{id}:
            parameters:
              - { name: id, in: path, required: true, schema: { type: string } }
            get:
              operationId: getCustomer
              summary: Get one customer
            delete:
              operationId: deleteCustomer
          /customers:
            get:
              operationId: listCustomers
              parameters:
                - { name: limit, in: query, schema: { type: integer } }
                - { name: X-Trace, in: header, schema: { type: string } }
            post:
              operationId: createCustomer
              requestBody:
                required: true
                content:
                  application/json:
                    schema: { $ref: "#/components/schemas/Customer" }
        components:
          schemas:
            Customer:
              type: object
              properties: { name: { type: string } }
        """;

    [Fact]
    public void Operations_become_tools_with_schemas_and_hints()
    {
        var ops = OpenApiImport.Load(Spec, "crm_");
        Assert.Equal(["crm_get_customer", "crm_delete_customer", "crm_list_customers", "crm_create_customer"], ops.Select(o => o.Name));

        var get = new OpenApiTool(ops[0], new Uri("https://crm.example/api"), new EgressPolicy(["crm.example"]), () => null);
        Assert.True(get.ProtocolTool.Annotations!.ReadOnlyHint);
        Assert.True(new OpenApiTool(ops[1], new Uri("https://crm.example/api"), new EgressPolicy([]), () => null).ProtocolTool.Annotations!.DestructiveHint);
        Assert.DoesNotContain("X-Trace", ops[2].InputSchema.ToJsonString()); // headers are not the model's to set
        Assert.Contains("\"name\"", ops[3].InputSchema["properties"]!["body"]!.ToJsonString()); // $ref inlined
    }

    [Fact]
    public void Path_parameters_are_escaped_so_a_call_cannot_leave_the_operation()
    {
        var op = OpenApiImport.Load(Spec)[0];
        var tool = new OpenApiTool(op, new Uri("https://crm.example/api"), new EgressPolicy(["crm.example"]), () => null);
        var args = new Dictionary<string, JsonElement> { ["id"] = JsonDocument.Parse("\"../admin?x=1\"").RootElement };

        var (uri, _) = tool.Build(args);

        Assert.Equal("https://crm.example/api/customers/..%2Fadmin%3Fx%3D1", uri.AbsoluteUri);
        Assert.Throws<ArgumentException>(() => tool.Build(new Dictionary<string, JsonElement>()));
    }
}
