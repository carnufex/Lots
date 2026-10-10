using Acme.Tickets.Mcp;

var builder = WebApplication.CreateBuilder(args);

// Replace TicketStore with a client for your real system. Give that client the narrowest credentials that do the job: the shell
// decides who may call which tool, but this server is what actually holds access to the backend.
builder.Services.AddSingleton<TicketStore>();
builder.Services.AddMcpServer().WithHttpTransport().WithTools<TicketTools>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapMcp("/mcp");
app.Run();
