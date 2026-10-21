using Microsoft.AspNetCore.Mvc;

namespace WebDS_Test.ViewComponents;

/// <summary>
/// Renders the department list for the main navigation dropdown. A view component rather than
/// inline markup so the navigation stays populated no matter which catalogue provider is
/// registered (local EF Core or Shopify).
/// </summary>
public sealed class CategoryMenuViewComponent : ViewComponent
{
    private readonly ICatalogService _catalog;

    public CategoryMenuViewComponent(ICatalogService catalog) => _catalog = catalog;

    public async Task<IViewComponentResult> InvokeAsync(int take = 12)
    {
        var categories = await _catalog.GetCategoryTreeAsync();

        return View(categories.Take(take).ToList());
    }
}
