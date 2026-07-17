using Microsoft.EntityFrameworkCore;

namespace Atlas.Api;

/// <summary>A validated page request; Page is 1-based.</summary>
public sealed record PageRequest(int Page, int PageSize)
{
    public int Skip => (Page - 1) * PageSize;
}

/// <summary>
/// Header-based pagination for list endpoints: bodies stay plain JSON arrays and
/// the page metadata travels in X-Total-Count / X-Page / X-Page-Size headers,
/// so existing consumers keep working while large collections stay bounded.
/// </summary>
public static class Pagination
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    /// <summary>Validates page/pageSize; returns a 400 ValidationProblem or null when valid.</summary>
    public static IResult? Validate(int? page, int? pageSize, out PageRequest paging)
    {
        var errors = new Dictionary<string, string[]>();
        if (page is < 1)
        {
            errors["page"] = ["page must be at least 1."];
        }
        if (pageSize is < 1 or > MaxPageSize)
        {
            errors["pageSize"] = [$"pageSize must be between 1 and {MaxPageSize}."];
        }

        paging = new PageRequest(page ?? 1, pageSize ?? DefaultPageSize);
        return errors.Count > 0 ? Results.ValidationProblem(errors) : null;
    }

    /// <summary>
    /// Counts the query, emits the pagination headers, and returns one page of
    /// results (ordering must already be applied).
    /// </summary>
    public static async Task<List<T>> ToPageAsync<T>(this IQueryable<T> query, HttpContext http, PageRequest paging)
    {
        var total = await query.CountAsync();
        http.Response.Headers["X-Total-Count"] = total.ToString();
        http.Response.Headers["X-Page"] = paging.Page.ToString();
        http.Response.Headers["X-Page-Size"] = paging.PageSize.ToString();
        return await query.Skip(paging.Skip).Take(paging.PageSize).ToListAsync();
    }
}
