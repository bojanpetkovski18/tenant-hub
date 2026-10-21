using TenantHub.Application.Paging;

namespace TenantHub.Application.Tests.Paging;

public sealed class PageRequestTests
{
    [Theory]
    [InlineData(null, null, 1, 25)]
    [InlineData(3, 50, 3, 50)]
    [InlineData(0, 0, 1, 25)]
    [InlineData(-4, -1, 1, 25)]
    [InlineData(2, 100, 2, 100)]
    [InlineData(2, 101, 2, 100)]
    [InlineData(1, 1, 1, 1)]
    public void Input_is_clamped_to_the_allowed_range(int? page, int? pageSize, int expectedPage, int expectedPageSize)
    {
        var request = PageRequest.From(page, pageSize);

        Assert.Equal(expectedPage, request.Page);
        Assert.Equal(expectedPageSize, request.PageSize);
    }

    [Theory]
    [InlineData(1, 25, 0)]
    [InlineData(2, 25, 25)]
    [InlineData(4, 10, 30)]
    public void Skip_is_the_number_of_rows_before_the_page(int page, int pageSize, int expectedSkip) =>
        Assert.Equal(expectedSkip, PageRequest.From(page, pageSize).Skip);
}
