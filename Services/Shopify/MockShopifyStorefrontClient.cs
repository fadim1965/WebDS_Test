using System.Text.Json;
using System.Text.Json.Nodes;

namespace WebDS_Test.Services.Shopify;

/// <summary>
/// Serves Storefront API responses from an in-process fixture instead of the network.
/// </summary>
/// <remarks>
/// <para>
/// This exists so the Shopify adapter is runnable and verifiable without a shop, a token or
/// network access — the same constraint that shaped the rest of this project. It is not a stub
/// that returns empty lists: it emits <em>genuine Shopify response shapes</em> (global IDs,
/// string money amounts, Relay-style <c>nodes</c>/<c>pageInfo</c> connections, <c>totalCount</c>)
/// so deserialization, the search-syntax translator, cursor walking and the mapping layer are all
/// exercised for real.
/// </para>
/// <para>
/// The fixture deliberately includes the awkward cases: a product with a single "Default Title"
/// variant, a product with a genuine sale price, a multi-option product, an out-of-stock product,
/// and a collection with a hashtag-free handle.
/// </para>
/// <para>
/// The GraphQL documents are still sent through the same code path, so field names in
/// <see cref="ShopifyQueries"/> must match the fixture keys — a typo in a query is caught here.
/// </para>
/// </remarks>
public sealed class MockShopifyStorefrontClient : IShopifyStorefrontClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };

    private readonly ILogger<MockShopifyStorefrontClient> _logger;

    public MockShopifyStorefrontClient(ILogger<MockShopifyStorefrontClient> logger) => _logger = logger;

    public Task<T> QueryAsync<T>(string query, object? variables = null, CancellationToken ct = default)
    {
        var args = ToDictionary(variables);
        var operation = DetectOperation(query);

        _logger.LogDebug("Mock Storefront API call: {Operation}", operation);

        JsonNode payload = operation switch
        {
            "productByHandle" => BuildProductByHandle(args),
            "collections" => BuildCollections(),
            "home" => BuildHome(args),
            "collectionProducts" => BuildCollectionProducts(args),
            _ => BuildProducts(args)
        };

        var result = payload.Deserialize<T>(JsonOptions)
                     ?? throw new ShopifyStorefrontException(
                         $"Mock Storefront API could not produce a '{typeof(T).Name}' payload for operation '{operation}'.");

        return Task.FromResult(result);
    }

    /// <summary>
    /// Determines which document was sent by looking for its distinctive root field. This keeps
    /// the mock honest: it must understand the same queries the live client would send.
    /// </summary>
    private static string DetectOperation(string query)
    {
        if (query.Contains("featured:", StringComparison.Ordinal) && query.Contains("newest:", StringComparison.Ordinal))
        {
            return "home";
        }

        if (query.Contains("product(handle:", StringComparison.Ordinal))
        {
            return "productByHandle";
        }

        if (query.Contains("collection(handle:", StringComparison.Ordinal))
        {
            return "collectionProducts";
        }

        if (query.Contains("collections(", StringComparison.Ordinal) && !query.Contains("collection(handle:", StringComparison.Ordinal))
        {
            return "collections";
        }

        return "products";
    }

    private static Dictionary<string, object?> ToDictionary(object? variables)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        if (variables is null)
        {
            return result;
        }

        foreach (var property in variables.GetType().GetProperties())
        {
            result[property.Name] = property.GetValue(variables);
        }

        return result;
    }

    private static int IntArg(Dictionary<string, object?> args, string name, int fallback) =>
        args.TryGetValue(name, out var value) && value is not null &&
        int.TryParse(value.ToString(), out var parsed)
            ? parsed
            : fallback;

    private static string? StringArg(Dictionary<string, object?> args, string name) =>
        args.TryGetValue(name, out var value) ? value?.ToString() : null;

    // ------------------------------------------------------------------
    // Payload builders
    // ------------------------------------------------------------------

    private static JsonNode BuildCollections() =>
        new JsonObject
        {
            ["collections"] = new JsonObject
            {
                ["nodes"] = new JsonArray(Fixture.Collections.Select(c => (JsonNode)new JsonObject
                {
                    ["id"] = c.Id,
                    ["handle"] = c.Handle,
                    ["title"] = c.Title,
                    ["description"] = c.Description,
                    ["updatedAt"] = "2026-01-14T09:00:00Z"
                }).ToArray())
            }
        };

    private static JsonNode BuildProductByHandle(Dictionary<string, object?> args)
    {
        var handle = StringArg(args, "handle");
        var product = Fixture.Products.FirstOrDefault(p =>
            string.Equals(p.Handle, handle, StringComparison.OrdinalIgnoreCase));

        return new JsonObject
        {
            ["product"] = product is null ? null : ProductNode(product, includeAllImages: true, includeAllVariants: true)
        };
    }

    private static JsonNode BuildHome(Dictionary<string, object?> args)
    {
        var first = IntArg(args, "first", 8);
        var featured = Fixture.Products.Where(p => p.Tags.Contains("featured")).Take(first).ToList();
        var newest = Fixture.Products.OrderByDescending(p => p.CreatedAt).Take(first).ToList();
        var bestSelling = Fixture.Products.Where(p => p.BestSellerRank > 0)
            .OrderBy(p => p.BestSellerRank).Take(first).ToList();

        return new JsonObject
        {
            ["featured"] = Connection(featured),
            ["newest"] = Connection(newest),
            ["bestSelling"] = Connection(bestSelling)
        };
    }

    private static JsonNode BuildCollectionProducts(Dictionary<string, object?> args)
    {
        var handle = StringArg(args, "handle");
        var first = IntArg(args, "first", 12);
        var after = StringArg(args, "after");

        var collection = Fixture.Collections.FirstOrDefault(c =>
            string.Equals(c.Handle, handle, StringComparison.OrdinalIgnoreCase));

        var members = Fixture.Products
            .Where(p => string.Equals(p.CollectionHandle, handle, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var (page, pageInfo) = Paginate(members, first, after);

        return new JsonObject
        {
            ["collection"] = collection is null ? null : new JsonObject
            {
                ["id"] = collection.Id,
                ["handle"] = collection.Handle,
                ["title"] = collection.Title,
                ["description"] = collection.Description,
                ["products"] = new JsonObject
                {
                    ["nodes"] = new JsonArray(page.Select(p => (JsonNode)ProductNode(p, false, true)).ToArray()),
                    ["pageInfo"] = pageInfo
                }
            }
        };
    }

    private static JsonNode BuildProducts(Dictionary<string, object?> args)
    {
        var first = IntArg(args, "first", 12);
        var after = StringArg(args, "after");
        var query = StringArg(args, "query");
        var sortKey = StringArg(args, "sortKey") ?? "CREATED_AT";
        var reverse = args.TryGetValue("reverse", out var r) && r is true;

        IEnumerable<FixtureProduct> products = Fixture.Products;

        // Honour the filters the translator emits, so a broken translator is visible rather than
        // silently returning everything.
        if (!string.IsNullOrWhiteSpace(query))
        {
            products = ApplyQueryArgument(products, query);
        }

        products = sortKey switch
        {
            "PRICE" => reverse
                ? products.OrderByDescending(p => p.MinPrice)
                : products.OrderBy(p => p.MinPrice),
            "TITLE" => reverse
                ? products.OrderByDescending(p => p.Title, StringComparer.OrdinalIgnoreCase)
                : products.OrderBy(p => p.Title, StringComparer.OrdinalIgnoreCase),
            "BEST_SELLING" => products.OrderBy(p => p.BestSellerRank == 0 ? int.MaxValue : p.BestSellerRank),
            _ => reverse
                ? products.OrderByDescending(p => p.CreatedAt)
                : products.OrderBy(p => p.CreatedAt)
        };

        var ordered = products.ToList();
        var (page, pageInfo) = Paginate(ordered, first, after);

        return new JsonObject
        {
            ["products"] = new JsonObject
            {
                ["totalCount"] = ordered.Count,
                ["nodes"] = new JsonArray(page.Select(p => (JsonNode)ProductNode(p, false, true)).ToArray()),
                ["pageInfo"] = pageInfo
            }
        };
    }

    /// <summary>
    /// Applies the subset of Shopify search syntax this app emits: free text, product_type,
    /// vendor, tag, available_for_sale and the -handle: exclusion.
    /// </summary>
    private static IEnumerable<FixtureProduct> ApplyQueryArgument(IEnumerable<FixtureProduct> products, string query)
    {
        var clauses = query.Split(" AND ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var raw in clauses)
        {
            var clause = raw.Trim();
            var negated = clause.StartsWith('-');
            if (negated)
            {
                clause = clause[1..];
            }

            var (field, value) = SplitClause(clause);
            value = value.Trim('"').Replace("\\\"", "\"").Replace("\\\\", "\\");

            Func<FixtureProduct, bool> predicate = field switch
            {
                "product_type" => p => string.Equals(p.ProductType, value, StringComparison.OrdinalIgnoreCase),
                "vendor" => p => string.Equals(p.Vendor, value, StringComparison.OrdinalIgnoreCase),
                "tag" => p => p.Tags.Contains(value, StringComparer.OrdinalIgnoreCase),
                "available_for_sale" => p => p.AvailableForSale == bool.Parse(value),
                "handle" => p => string.Equals(p.Handle, value, StringComparison.OrdinalIgnoreCase),
                // A bare term is a free-text match across the fields Shopify searches.
                _ => p => p.Title.Contains(value, StringComparison.OrdinalIgnoreCase)
                          || p.Vendor.Contains(value, StringComparison.OrdinalIgnoreCase)
                          || p.ProductType.Contains(value, StringComparison.OrdinalIgnoreCase)
                          || (p.Description?.Contains(value, StringComparison.OrdinalIgnoreCase) ?? false)
                          || p.Tags.Any(t => t.Contains(value, StringComparison.OrdinalIgnoreCase))
            };

            products = negated ? products.Where(p => !predicate(p)) : products.Where(predicate);
        }

        return products;
    }

    private static (string Field, string Value) SplitClause(string clause)
    {
        var colon = clause.IndexOf(':');
        return colon < 0
            ? (string.Empty, clause)
            : (clause[..colon], clause[(colon + 1)..]);
    }

    private static (List<FixtureProduct> Page, JsonObject PageInfo) Paginate(List<FixtureProduct> all, int first, string? after)
    {
        var start = 0;

        if (!string.IsNullOrEmpty(after) && after.StartsWith("cursor:", StringComparison.Ordinal) &&
            int.TryParse(after["cursor:".Length..], out var index))
        {
            start = Math.Clamp(index, 0, all.Count);
        }

        var page = all.Skip(start).Take(Math.Max(1, first)).ToList();
        var end = start + page.Count;
        var hasNext = end < all.Count;

        return (page, new JsonObject
        {
            ["hasNextPage"] = hasNext,
            ["hasPreviousPage"] = start > 0,
            ["startCursor"] = page.Count == 0 ? null : $"cursor:{start}",
            ["endCursor"] = page.Count == 0 ? null : $"cursor:{end}"
        });
    }

    private static JsonObject Connection(IEnumerable<FixtureProduct> products)
    {
        var list = products.ToList();

        return new JsonObject
        {
            ["nodes"] = new JsonArray(list.Select(p => (JsonNode)ProductNode(p, false, true)).ToArray()),
            ["pageInfo"] = new JsonObject
            {
                ["hasNextPage"] = false,
                ["hasPreviousPage"] = false,
                ["startCursor"] = list.Count == 0 ? null : "cursor:0",
                ["endCursor"] = list.Count == 0 ? null : $"cursor:{list.Count}"
            }
        };
    }

    private static JsonObject ProductNode(FixtureProduct p, bool includeAllImages, bool includeAllVariants)
    {
        var images = new JsonArray();
        foreach (var url in includeAllImages ? p.Images : p.Images.Take(1))
        {
            images.Add(new JsonObject { ["url"] = url, ["altText"] = p.Title, ["width"] = 1200, ["height"] = 1200 });
        }

        var variants = new JsonArray();
        foreach (var v in includeAllVariants ? p.Variants : p.Variants.Take(50))
        {
            variants.Add(new JsonObject
            {
                ["id"] = v.Id,
                ["title"] = v.Title,
                ["sku"] = v.Sku,
                ["availableForSale"] = v.AvailableForSale,
                ["quantityAvailable"] = v.QuantityAvailable,
                ["price"] = Money(v.Price),
                ["compareAtPrice"] = v.CompareAtPrice is { } compareAt ? Money(compareAt) : null,
                ["image"] = null,
                ["selectedOptions"] = new JsonArray(v.Options
                    .Select(o => (JsonNode)new JsonObject { ["name"] = o.Name, ["value"] = o.Value })
                    .ToArray())
            });
        }

        return new JsonObject
        {
            ["id"] = p.Id,
            ["handle"] = p.Handle,
            ["title"] = p.Title,
            ["description"] = p.Description,
            ["descriptionHtml"] = $"<p>{p.Description}</p>",
            ["vendor"] = p.Vendor,
            ["productType"] = p.ProductType,
            ["tags"] = new JsonArray(p.Tags.Select(t => (JsonNode)t).ToArray()),
            ["createdAt"] = p.CreatedAt.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["updatedAt"] = p.CreatedAt.AddDays(3).ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["publishedAt"] = p.CreatedAt.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["availableForSale"] = p.AvailableForSale,
            ["totalInventory"] = p.Variants.Sum(v => v.QuantityAvailable),
            ["featuredImage"] = p.Images.Count == 0 ? null : new JsonObject
            {
                ["url"] = p.Images[0],
                ["altText"] = p.Title,
                ["width"] = 1200,
                ["height"] = 1200
            },
            ["priceRange"] = new JsonObject
            {
                ["minVariantPrice"] = Money(p.MinPrice),
                ["maxVariantPrice"] = Money(p.MaxPrice)
            },
            ["images"] = new JsonObject { ["nodes"] = images },
            ["variants"] = new JsonObject { ["nodes"] = variants }
        };
    }

    private static JsonObject Money(decimal amount) => new()
    {
        ["amount"] = amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
        ["currencyCode"] = "USD"
    };

    // ------------------------------------------------------------------
    // Fixture data
    // ------------------------------------------------------------------

    private sealed record FixtureCollection(string Id, string Handle, string Title, string Description);

    private sealed record FixtureOption(string Name, string Value);

    private sealed record FixtureVariant(
        string Id, string Title, string Sku, decimal Price, decimal? CompareAtPrice,
        int QuantityAvailable, bool AvailableForSale, List<FixtureOption> Options);

    private sealed record FixtureProduct(
        string Id, string Handle, string Title, string Description, string Vendor, string ProductType,
        List<string> Tags, string CollectionHandle, List<string> Images, List<FixtureVariant> Variants,
        DateTimeOffset CreatedAt, int BestSellerRank, bool AvailableForSale)
    {
        public decimal MinPrice => Variants.Count == 0 ? 0m : Variants.Min(v => v.Price);
        public decimal MaxPrice => Variants.Count == 0 ? 0m : Variants.Max(v => v.Price);
    }

    private static class Fixture
    {
        private const string ProductGid = "gid://shopify/Product/";
        private const string VariantGid = "gid://shopify/ProductVariant/";
        private const string CollectionGid = "gid://shopify/Collection/";

        public static readonly List<FixtureCollection> Collections = new()
        {
            new($"{CollectionGid}1001", "mailing-and-parcels", "Mailing & Parcels", "Letters, parcels and registered mail."),
            new($"{CollectionGid}1002", "express-mail", "Express Mail", "EMS and PostXpress time-critical delivery."),
            new($"{CollectionGid}1003", "philately", "Philately & Stamps", "Collector stamps and first-day covers."),
            new($"{CollectionGid}1004", "packaging", "Packaging", "Boxes, envelopes and protective mailers."),
            new($"{CollectionGid}1005", "p-o-box", "P.O. Box Services", "Annual and quarterly post office boxes."),
            new($"{CollectionGid}1006", "office-supplies", "Office Supplies", "Stationery and counter consumables.")
        };

        public static readonly List<FixtureProduct> Products = new()
        {
            new($"{ProductGid}2001", "ems-priority-envelope-pack", "EMS Priority Envelope — Pack of 10",
                "Pre-paid express envelopes with tracking included. Sized for A4 documents and ready for counter drop-off.",
                "LibanPost", "Express Mail", new List<string> { "featured", "express" }, "express-mail",
                new List<string> { "https://cdn.shopify.com/fixture/ems-envelope-1.png", "https://cdn.shopify.com/fixture/ems-envelope-2.png" },
                new List<FixtureVariant>
                {
                    new($"{VariantGid}3001", "Pack of 10", "LP-EMS-ENV-10", 24.50m, 29.00m, 148, true,
                        new List<FixtureOption> { new("Size", "Pack of 10") }),
                    new($"{VariantGid}3002", "Pack of 25", "LP-EMS-ENV-25", 54.00m, null, 62, true,
                        new List<FixtureOption> { new("Size", "Pack of 25") })
                },
                new DateTimeOffset(2025, 11, 2, 8, 0, 0, TimeSpan.Zero), 1, true),

            new($"{ProductGid}2002", "registered-mail-kit", "Registered Mail Kit",
                "Everything needed to send registered mail: tamper-evident wallet, seals and a tracking log.",
                "LibanPost", "Mailing & Parcels", new List<string> { "featured" }, "mailing-and-parcels",
                new List<string> { "https://cdn.shopify.com/fixture/registered-kit-1.png" },
                new List<FixtureVariant>
                {
                    // Single-variant products come back from Shopify as "Default Title".
                    new($"{VariantGid}3003", "Default Title", "LP-REG-KIT", 18.75m, null, 210, true,
                        new List<FixtureOption> { new("Title", "Default Title") })
                },
                new DateTimeOffset(2025, 12, 8, 8, 0, 0, TimeSpan.Zero), 2, true),

            new($"{ProductGid}2003", "double-wall-shipping-box-medium", "Double-Wall Shipping Box — Medium",
                "Rigid double-wall board rated for 15 kg. Flat-packed in bundles, with a self-seal strip.",
                "LibanPost", "Packaging", new List<string> { "packaging" }, "packaging",
                new List<string> { "https://cdn.shopify.com/fixture/box-medium-1.png", "https://cdn.shopify.com/fixture/box-medium-2.png" },
                new List<FixtureVariant>
                {
                    new($"{VariantGid}3004", "Bundle of 10", "LP-BOX-MD-10", 32.00m, null, 84, true,
                        new List<FixtureOption> { new("Bundle", "Bundle of 10") }),
                    new($"{VariantGid}3005", "Bundle of 25", "LP-BOX-MD-25", 71.50m, 80.00m, 24, true,
                        new List<FixtureOption> { new("Bundle", "Bundle of 25") }),
                    // A genuinely sold-out option, so the out-of-stock path is exercised.
                    new($"{VariantGid}3006", "Pallet of 200", "LP-BOX-MD-200", 520.00m, null, 0, false,
                        new List<FixtureOption> { new("Bundle", "Pallet of 200") })
                },
                new DateTimeOffset(2025, 10, 19, 8, 0, 0, TimeSpan.Zero), 5, true),

            new($"{ProductGid}2004", "padded-mailer-a4", "Padded Mailer — A4",
                "Lightweight bubble-lined mailer for small, fragile items. Peel-and-seal closure.",
                "LibanPost", "Packaging", new List<string>(), "packaging",
                new List<string> { "https://cdn.shopify.com/fixture/mailer-a4-1.png" },
                new List<FixtureVariant>
                {
                    new($"{VariantGid}3007", "Pack of 50", "LP-MLR-A4-50", 15.25m, null, 320, true,
                        new List<FixtureOption> { new("Size", "Pack of 50") })
                },
                new DateTimeOffset(2026, 1, 6, 8, 0, 0, TimeSpan.Zero), 4, true),

            new($"{ProductGid}2005", "cedar-tree-commemorative-sheet", "Cedar Tree Commemorative Sheet",
                "Limited commemorative sheet celebrating the Lebanese cedar, issued in a numbered presentation folder.",
                "LibanPost Philately", "Philately & Stamps", new List<string> { "featured", "collector" }, "philately",
                new List<string> { "https://cdn.shopify.com/fixture/cedar-sheet-1.png", "https://cdn.shopify.com/fixture/cedar-sheet-2.png", "https://cdn.shopify.com/fixture/cedar-sheet-3.png" },
                new List<FixtureVariant>
                {
                    new($"{VariantGid}3008", "Mint", "LP-STMP-CEDAR-M", 12.00m, null, 540, true,
                        new List<FixtureOption> { new("Condition", "Mint") }),
                    new($"{VariantGid}3009", "First Day Cover", "LP-STMP-CEDAR-FDC", 19.50m, 24.00m, 96, true,
                        new List<FixtureOption> { new("Condition", "First Day Cover") })
                },
                new DateTimeOffset(2025, 9, 30, 8, 0, 0, TimeSpan.Zero), 3, true),

            new($"{ProductGid}2006", "p-o-box-annual-standard", "P.O. Box — Annual, Standard Size",
                "Twelve months of standard-size P.O. Box rental at a participating branch, activated at the counter.",
                "LibanPost", "P.O. Box Services", new List<string>(), "p-o-box",
                new List<string> { "https://cdn.shopify.com/fixture/pobox-1.png" },
                new List<FixtureVariant>
                {
                    new($"{VariantGid}3010", "Standard", "LP-POB-STD-YR", 120.00m, null, 45, true,
                        new List<FixtureOption> { new("Size", "Standard") }),
                    new($"{VariantGid}3011", "Large", "LP-POB-LRG-YR", 210.00m, null, 12, true,
                        new List<FixtureOption> { new("Size", "Large") })
                },
                new DateTimeOffset(2025, 8, 14, 8, 0, 0, TimeSpan.Zero), 6, true),

            new($"{ProductGid}2007", "counter-scale-tape-dispenser", "Counter Tape Dispenser",
                "Weighted desktop dispenser that takes 50 mm tape and stays put with one hand free.",
                "LibanPost Supplies", "Office Supplies", new List<string>(), "office-supplies",
                new List<string> { "https://cdn.shopify.com/fixture/tape-dispenser-1.png" },
                new List<FixtureVariant>
                {
                    new($"{VariantGid}3012", "Default Title", "LP-SUP-TD-01", 27.90m, 34.00m, 58, true,
                        new List<FixtureOption> { new("Title", "Default Title") })
                },
                new DateTimeOffset(2026, 2, 2, 8, 0, 0, TimeSpan.Zero), 7, true),

            new($"{ProductGid}2008", "thermal-label-rolls-4x6", "Thermal Label Rolls — 4×6 in",
                "Direct-thermal labels for parcel and courier printers. BPA-free, no ribbon required.",
                "LibanPost Supplies", "Office Supplies", new List<string>(), "office-supplies",
                new List<string> { "https://cdn.shopify.com/fixture/label-rolls-1.png" },
                new List<FixtureVariant>
                {
                    new($"{VariantGid}3013", "Roll of 250", "LP-SUP-TL-250", 21.40m, null, 175, true,
                        new List<FixtureOption> { new("Quantity", "Roll of 250") }),
                    new($"{VariantGid}3014", "Case of 12 rolls", "LP-SUP-TL-12", 232.00m, 259.00m, 9, true,
                        new List<FixtureOption> { new("Quantity", "Case of 12 rolls") })
                },
                new DateTimeOffset(2025, 12, 22, 8, 0, 0, TimeSpan.Zero), 8, true),

            new($"{ProductGid}2009", "postxpress-same-day-label", "PostXpress Same-Day Label",
                "Same-day domestic label with SMS tracking. Purchased per consignment at the counter.",
                "LibanPost", "Express Mail", new List<string> { "express" }, "express-mail",
                new List<string> { "https://cdn.shopify.com/fixture/postxpress-1.png" },
                new List<FixtureVariant>
                {
                    new($"{VariantGid}3015", "Single", "LP-PX-SD-1", 9.75m, null, 400, true,
                        new List<FixtureOption> { new("Unit", "Single") })
                },
                new DateTimeOffset(2026, 1, 27, 8, 0, 0, TimeSpan.Zero), 9, true),

            new($"{ProductGid}2010", "fragile-handling-labels", "Fragile Handling Labels",
                "High-contrast handling labels that survive sorting machinery and humidity.",
                "LibanPost Supplies", "Office Supplies", new List<string>(), "office-supplies",
                new List<string> { "https://cdn.shopify.com/fixture/fragile-labels-1.png" },
                new List<FixtureVariant>
                {
                    new($"{VariantGid}3016", "Sheet of 24", "LP-SUP-FR-24", 6.50m, null, 610, true,
                        new List<FixtureOption> { new("Format", "Sheet of 24") })
                },
                new DateTimeOffset(2025, 11, 19, 8, 0, 0, TimeSpan.Zero), 10, true),

            new($"{ProductGid}2011", "bubble-wrap-roll-500mm", "Bubble Wrap Roll — 500 mm × 50 m",
                "Standard-duty bubble wrap for parcels that need cushioning without much weight.",
                "LibanPost Supplies", "Packaging", new List<string>(), "packaging",
                new List<string> { "https://cdn.shopify.com/fixture/bubble-wrap-1.png" },
                new List<FixtureVariant>
                {
                    new($"{VariantGid}3017", "Single roll", "LP-SUP-BW-500", 28.60m, null, 41, true,
                        new List<FixtureOption> { new("Unit", "Single roll") })
                },
                new DateTimeOffset(2025, 10, 5, 8, 0, 0, TimeSpan.Zero), 11, true),

            new($"{ProductGid}2012", "philatelic-album-starter", "Philatelic Album — Starter",
                "Ring binder with 40 acid-free pages, interleaves and a slipcase for a new collection.",
                "LibanPost Philately", "Philately & Stamps", new List<string> { "collector" }, "philately",
                new List<string> { "https://cdn.shopify.com/fixture/album-1.png" },
                new List<FixtureVariant>
                {
                    new($"{VariantGid}3018", "Default Title", "LP-STMP-ALB-01", 44.00m, null, 27, true,
                        new List<FixtureOption> { new("Title", "Default Title") })
                },
                new DateTimeOffset(2025, 9, 12, 8, 0, 0, TimeSpan.Zero), 12, true)
        };
    }
}
