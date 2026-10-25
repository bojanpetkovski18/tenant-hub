namespace TenantHub.Application.Paging;

/// <summary>One page of a list (SPEC 7): the items plus what the UI needs to page.</summary>
public sealed record PagedList<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);
