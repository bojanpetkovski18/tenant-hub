namespace TenantHub.Application.Paging;

/// <summary>
/// Paging input for list queries (SPEC section 7): 1-based <c>page</c>, <c>pageSize</c> defaulting to 25 and
/// capped at 100. Out-of-range input is clamped rather than rejected, so a hand-edited URL still shows a list.
/// </summary>
public readonly record struct PageRequest
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;

    private PageRequest(int page, int pageSize)
    {
        Page = page;
        PageSize = pageSize;
    }

    public int Page { get; }

    public int PageSize { get; }

    public int Skip => (Page - 1) * PageSize;

    public static PageRequest From(int? page, int? pageSize) =>
        new(
            page is null or < 1 ? 1 : page.Value,
            pageSize switch
            {
                null or < 1 => DefaultPageSize,
                > MaxPageSize => MaxPageSize,
                _ => pageSize.Value,
            });
}
