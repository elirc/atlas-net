using Atlas.Domain.Entities;
using Atlas.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Atlas.Api.Endpoints;

public static class ComplianceEndpoints
{
    public static IEndpointRouteBuilder MapComplianceEndpoints(this IEndpointRouteBuilder app)
    {
        var workerDocs = app.MapGroup("/api/workers/{workerId:guid}/documents");

        workerDocs.MapGet("/", async (Guid workerId, AtlasDbContext db) =>
        {
            if (!await db.Workers.AnyAsync(w => w.Id == workerId))
            {
                return Results.NotFound();
            }

            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var documents = await db.ComplianceDocuments
                .Where(d => d.WorkerId == workerId)
                .OrderBy(d => d.ExpiryDate)
                .ToListAsync();

            return Results.Ok(documents.Select(d => ToResponse(d, today)).ToList());
        });

        workerDocs.MapPost("/", async (Guid workerId, CreateComplianceDocumentRequest request, AtlasDbContext db) =>
        {
            if (!await db.Workers.AnyAsync(w => w.Id == workerId))
            {
                return Results.NotFound();
            }

            var errors = new Dictionary<string, string[]>();
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                errors["name"] = ["Name is required."];
            }
            if (string.IsNullOrWhiteSpace(request.Type)
                || !Enum.TryParse<ComplianceDocumentType>(request.Type, ignoreCase: true, out _))
            {
                errors["type"] = [$"Type must be one of: {string.Join(", ", Enum.GetNames<ComplianceDocumentType>())}."];
            }
            if (request.IssuedDate is not null && request.ExpiryDate is not null
                && request.ExpiryDate < request.IssuedDate)
            {
                errors["expiryDate"] = ["ExpiryDate cannot be before IssuedDate."];
            }
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var document = new ComplianceDocument
            {
                WorkerId = workerId,
                Type = Enum.Parse<ComplianceDocumentType>(request.Type!, ignoreCase: true),
                Name = request.Name!.Trim(),
                IssuedDate = request.IssuedDate,
                ExpiryDate = request.ExpiryDate,
            };
            db.ComplianceDocuments.Add(document);
            await db.SaveChangesAsync();

            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            return Results.Created($"/api/workers/{workerId}/documents/{document.Id}", ToResponse(document, today));
        });

        app.MapGet("/api/compliance/expiring", async (int? withinDays, AtlasDbContext db) =>
        {
            var window = withinDays ?? ComplianceDocument.DefaultExpiringSoonWindowDays;
            if (window < 0)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["withinDays"] = ["withinDays must be non-negative."],
                });
            }

            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var cutoff = today.AddDays(window);

            var expiring = await db.ComplianceDocuments
                .Include(d => d.Worker)
                .Where(d => d.ExpiryDate != null && d.ExpiryDate <= cutoff)
                .OrderBy(d => d.ExpiryDate)
                .ToListAsync();

            return Results.Ok(expiring
                .Select(d => new ExpiringDocumentResponse(
                    d.Id,
                    d.WorkerId,
                    d.Worker!.FullName,
                    d.Type.ToString(),
                    d.Name,
                    d.ExpiryDate!.Value,
                    d.GetStatus(today, window).ToString()))
                .ToList());
        });

        return app;
    }

    private static ComplianceDocumentResponse ToResponse(ComplianceDocument d, DateOnly asOf) =>
        new(d.Id, d.WorkerId, d.Type.ToString(), d.Name, d.IssuedDate, d.ExpiryDate, d.GetStatus(asOf).ToString(), d.CreatedAtUtc);
}

public record CreateComplianceDocumentRequest(
    string? Type,
    string? Name,
    DateOnly? IssuedDate,
    DateOnly? ExpiryDate);

public record ComplianceDocumentResponse(
    Guid Id,
    Guid WorkerId,
    string Type,
    string Name,
    DateOnly? IssuedDate,
    DateOnly? ExpiryDate,
    string Status,
    DateTimeOffset CreatedAtUtc);

public record ExpiringDocumentResponse(
    Guid Id,
    Guid WorkerId,
    string WorkerName,
    string Type,
    string Name,
    DateOnly ExpiryDate,
    string Status);
