using Atlas.Domain.Entities;
using Atlas.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Atlas.Api.Endpoints;

public static class InvoiceEndpoints
{
    public static IEndpointRouteBuilder MapInvoiceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/invoices");

        group.MapGet("/", async (Guid? clientId, AtlasDbContext db) =>
        {
            var query = db.Invoices.AsQueryable();
            if (clientId is not null)
            {
                query = query.Where(i => i.ClientId == clientId);
            }

            var invoices = await query.OrderBy(i => i.InvoiceNumber).ToListAsync();
            return Results.Ok(invoices.Select(ToResponse).ToList());
        });

        group.MapGet("/{id:guid}", async (Guid id, AtlasDbContext db) =>
        {
            var invoice = await db.Invoices.FindAsync(id);
            return invoice is null ? Results.NotFound() : Results.Ok(ToResponse(invoice));
        });

        return app;
    }

    internal static InvoiceResponse ToResponse(Invoice i) => new(
        i.Id,
        i.InvoiceNumber,
        i.ClientId,
        i.PayrollRunId,
        i.CurrencyCode,
        i.PayrollSubtotal,
        i.ManagementFee,
        i.Total,
        i.IssuedAtUtc);
}

public record InvoiceResponse(
    Guid Id,
    string InvoiceNumber,
    Guid ClientId,
    Guid PayrollRunId,
    string CurrencyCode,
    decimal PayrollSubtotal,
    decimal ManagementFee,
    decimal Total,
    DateTimeOffset IssuedAtUtc);
