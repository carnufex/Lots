using Lots.Shell.Core.Audit;
using Lots.Shell.Features.Audit;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lots.Shell.Tests.Features;

public class AuditChainTests
{
    private static (LotsDbContext Db, AuditSealer Sealer, ServiceProvider Services) Setup()
    {
        var services = new ServiceCollection();
        var name = Guid.NewGuid().ToString();
        services.AddDbContext<LotsDbContext>(o => o.UseInMemoryDatabase(name));
        var provider = services.BuildServiceProvider();
        var db = provider.GetRequiredService<LotsDbContext>();
        var t = DateTimeOffset.UtcNow;
        for (var i = 0; i < 5; i++)
            db.AuditLog.Add(new AuditRecord { Id = Guid.NewGuid(), At = t.AddSeconds(i), UserId = "u", Tool = $"tool{i}", Decision = AuditDecision.Allowed, Reason = "ok" });
        db.SaveChanges();
        return (db, new AuditSealer(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<AuditSealer>.Instance), provider);
    }

    [Fact]
    public async Task Sealed_rows_form_an_intact_chain_and_new_rows_continue_it()
    {
        var (db, sealer, services) = Setup();
        Assert.Equal(5, await sealer.SealAsync(default));
        db.AuditLog.Add(new AuditRecord { Id = Guid.NewGuid(), At = DateTimeOffset.UtcNow.AddMinutes(1), UserId = "u", Tool = "later", Decision = AuditDecision.Denied });
        await db.SaveChangesAsync();
        Assert.Equal(1, await sealer.SealAsync(default));

        using var scope = services.CreateScope();
        var check = await AuditSealer.VerifyAsync(scope.ServiceProvider.GetRequiredService<LotsDbContext>(), default);
        Assert.True(check.Intact);
        Assert.Equal(6, check.Sealed);
    }

    [Fact]
    public async Task Changing_or_removing_a_sealed_row_is_detected_at_that_row()
    {
        var (_, sealer, services) = Setup();
        await sealer.SealAsync(default);

        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LotsDbContext>();
            var row = await db.AuditLog.SingleAsync(a => a.Seq == 3);
            row.Reason = "approved by nobody";   // someone edits history
            await db.SaveChangesAsync();
            var check = await AuditSealer.VerifyAsync(db, default);
            Assert.Equal((false, 3L, "the row's content was changed"), (check.Intact, check.FirstBrokenSeq!.Value, check.Problem));
        }
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LotsDbContext>();
            db.AuditLog.Remove(await db.AuditLog.SingleAsync(a => a.Seq == 2));
            await db.SaveChangesAsync();
            var check = await AuditSealer.VerifyAsync(db, default);
            Assert.Equal((false, 2L), (check.Intact, check.FirstBrokenSeq!.Value));
        }
    }

    [Fact]
    public void Csv_export_quotes_and_neutralises_spreadsheet_formulas()
    {
        Assert.Equal("\"a,b\"", ExportAuditEndpoint.Csv("a,b"));
        Assert.Equal("'=HYPERLINK(1)", ExportAuditEndpoint.Csv("=HYPERLINK(1)"));
        Assert.Equal("\"say \"\"hi\"\"\"", ExportAuditEndpoint.Csv("say \"hi\""));
        Assert.Contains("lots@32473 seq=\"7\"", AuditForwarder.Syslog(new AuditRecord { Id = Guid.NewGuid(), Seq = 7, UserId = "u", Tool = "t", At = DateTimeOffset.UtcNow }));
    }
}
