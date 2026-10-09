using System.Net;
using System.Text;
using Lots.Mcp.Homelab;

namespace Lots.Mcp.Homelab.Tests;

public class DockerApiTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpMethod> Methods { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Methods.Add(request.Method);
            return Task.FromResult(respond(request));
        }
    }

    private static byte[] Frame(byte stream, string text)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        var header = new byte[] { stream, 0, 0, 0, (byte)(payload.Length >> 24), (byte)(payload.Length >> 16), (byte)(payload.Length >> 8), (byte)payload.Length };
        return [.. header, .. payload];
    }

    private static DockerApi Api(Handler h) => new(new HttpClient(h) { BaseAddress = new Uri("http://docker.test/") });

    [Fact]
    public async Task Lists_containers_with_health_and_uses_only_GET()
    {
        var h = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                [{"Id":"abcdef1234567890","Names":["/web"],"Image":"nginx","State":"running","Status":"Up 2 hours (unhealthy)"},
                 {"Id":"1234567890abcdef","Names":["/db"],"Image":"postgres","State":"running","Status":"Up 2 hours (healthy)"}]
                """),
        });

        var list = await Api(h).ListContainersAsync(all: false, default);

        Assert.Equal("unhealthy", list.Single(c => c.Name == "web").Health);
        Assert.Equal("healthy", list.Single(c => c.Name == "db").Health);
        Assert.Equal("abcdef123456", list[0].Id);
        Assert.All(h.Methods, m => Assert.Equal(HttpMethod.Get, m));
    }

    [Fact]
    public void Demuxes_multiplexed_log_frames()
    {
        byte[] data = [.. Frame(1, "line one\n"), .. Frame(2, "error line\n")];

        Assert.Equal("line one\nerror line\n", DockerApi.DemuxFrames(data));
    }

    [Fact]
    public async Task Tty_containers_are_returned_raw()
    {
        var h = new Handler(r => r.RequestUri!.AbsolutePath.EndsWith("/json")
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"Config":{"Tty":true}}""") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("raw log\n") });

        Assert.Equal("raw log\n", await Api(h).GetLogsAsync("c", 10, default));
    }

    [Fact]
    public void Large_logs_keep_the_tail_and_say_so()
    {
        var text = string.Concat(Enumerable.Range(0, 20_000).Select(i => $"line {i}\n"));

        var result = DockerApi.Truncate(text);

        Assert.StartsWith("[truncated", result);
        Assert.EndsWith("line 19999\n", result);
        Assert.True(result.Length < DockerApi.MaxLogBytes + 200);
    }
}
