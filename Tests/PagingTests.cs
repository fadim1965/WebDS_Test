using WebDS_Test.Services;
using Xunit;

namespace WebDS_Test.Tests;

/// <summary>
/// Paging arithmetic guards against the two things that used to produce a 500 on the SQL Server
/// store: a page number from the query string that is below 1, and one so large that
/// <c>(page - 1) * pageSize</c> overflows into a negative OFFSET.
/// </summary>
public class PagingTests
{
    [Theory]
    [InlineData(1, 10, 100, 1)]
    [InlineData(5, 10, 100, 5)]
    [InlineData(10, 10, 100, 10)]
    [InlineData(11, 10, 100, 10)]   // past the end lands on the last page
    [InlineData(999999, 10, 100, 10)]
    [InlineData(0, 10, 100, 1)]     // below the first page lands on the first
    [InlineData(-7, 10, 100, 1)]
    [InlineData(1, 10, 0, 1)]       // an empty result still has a page 1
    public void ClampPage_always_returns_a_page_that_exists(int page, int size, int total, int expected) =>
        Assert.Equal(expected, Paging.ClampPage(page, size, total));

    [Fact]
    public void ClampPage_treats_a_zero_page_size_as_one_instead_of_dividing_by_zero()
    {
        var page = Paging.ClampPage(3, 0, 20);

        Assert.Equal(3, page);
    }

    [Fact]
    public void Skip_is_zero_on_the_first_page() => Assert.Equal(0, Paging.Skip(1, 20));

    [Theory]
    [InlineData(2, 20, 20)]
    [InlineData(3, 20, 40)]
    [InlineData(1, 25, 0)]
    public void Skip_is_page_minus_one_times_the_page_size(int page, int size, int expected) =>
        Assert.Equal(expected, Paging.Skip(page, size));

    [Fact]
    public void Skip_never_returns_a_negative_offset_for_a_nonsense_page()
    {
        Assert.Equal(0, Paging.Skip(0, 20));
        Assert.Equal(0, Paging.Skip(int.MinValue, 20));
    }

    [Fact]
    public void Skip_is_capped_rather_than_overflowing_for_an_absurd_page()
    {
        // (int.MaxValue - 1) * 100 overflows a signed 32-bit int; the cap keeps it a valid OFFSET.
        var skip = Paging.Skip(int.MaxValue, 100);

        Assert.Equal(int.MaxValue, skip);
        Assert.True(skip >= 0);
    }

    [Fact]
    public void A_clamped_page_and_its_skip_agree_on_where_the_rows_start()
    {
        const int size = 10;
        const int total = 25;

        var page = Paging.ClampPage(999, size, total);

        Assert.Equal(3, page);
        Assert.Equal(20, Paging.Skip(page, size));
    }
}
