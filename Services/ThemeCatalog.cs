using WebDS_Test.Domain;

namespace WebDS_Test.Services;

/// <summary>
/// The catalogue of storefront themes a vendor can choose from.
/// </summary>
/// <remarks>
/// Resolution always goes through <see cref="Get"/>, which falls back to the default theme, so an
/// unknown or empty <see cref="Shop.ThemeKey"/> can never produce an unstyled storefront.
/// </remarks>
public interface IThemeCatalog
{
    /// <summary>Every selectable theme, in the order the picker should show them.</summary>
    IReadOnlyList<ShopTheme> All { get; }

    /// <summary>The theme used when a shop has not chosen one, and the marketplace default.</summary>
    ShopTheme Default { get; }

    /// <summary>Resolves a stored key to a theme, falling back to <see cref="Default"/>.</summary>
    ShopTheme Get(string? key);

    /// <summary>True when the key names a real theme. Used to validate vendor input.</summary>
    bool Contains(string? key);
}

/// <summary>
/// In-memory theme catalogue. Themes are pure data, so this is a singleton with no dependencies.
/// </summary>
public sealed class ThemeCatalog : IThemeCatalog
{
    /// <summary>Key of the marketplace's own identity, used as the default template.</summary>
    public const string DefaultKey = "postal-classic";

    private static readonly IReadOnlyList<ShopTheme> Themes = new List<ShopTheme>
    {
        // The LibanPost identity: the same palette BrandingOptions ships with, so an unchosen shop
        // looks exactly like the marketplace.
        new()
        {
            Key = "postal-classic",
            Name = "Postal Classic",
            Description = "The LibanPost blue and yellow. Clean, corporate and familiar.",
            Tone = "Corporate",
            Primary = "#006db7",
            PrimaryDark = "#015a97",
            PrimaryDarker = "#0b5f98",
            PrimaryLight = "#2683c2",
            PrimaryTint = "#c9e0f0",
            Accent = "#fed300",
            AccentDark = "#e0b900",
            Ink = "#1a1a1a",
            InkMuted = "#2a4c62",
            InkSubtle = "#6b6b6b",
            Surface = "#ffffff",
            SurfaceMuted = "#f6f6f6",
            SurfaceRaised = "#f1f1f1",
            Border = "#e6e6e6",
            BorderStrong = "#d7d7d7",
            Danger = "#e33c3c",
            Success = "#2e7d32",
            Radius = 4,
            Shadow = "0 1px 3px rgba(0,0,0,.10)",
            FontStack = "'Libre Franklin','Open Sans','Segoe UI',Roboto,'Helvetica Neue',Arial,sans-serif"
        },

        new()
        {
            Key = "midnight",
            Name = "Midnight",
            Description = "Dark slate surfaces with an electric cyan accent. Suited to tech and gadgets.",
            Tone = "Dark",
            Primary = "#4f9cf9",
            PrimaryDark = "#2f7fe0",
            PrimaryDarker = "#1f66c2",
            PrimaryLight = "#7db8ff",
            PrimaryTint = "#12304f",
            Accent = "#22d3ee",
            AccentDark = "#06b6d4",
            Ink = "#f1f5f9",
            InkMuted = "#cbd5e1",
            InkSubtle = "#94a3b8",
            Surface = "#0f172a",
            SurfaceMuted = "#0b1220",
            SurfaceRaised = "#1e293b",
            Border = "#24314a",
            BorderStrong = "#33415f",
            Danger = "#f87171",
            Success = "#34d399",
            Radius = 10,
            Shadow = "0 6px 20px rgba(0,0,0,.45)",
            FontStack = "'Inter','Segoe UI',Roboto,Arial,sans-serif",
            IsDark = true
        },

        new()
        {
            Key = "sunrise",
            Name = "Sunrise",
            Description = "Warm oranges and soft creams. Friendly, food-friendly and inviting.",
            Tone = "Warm",
            Primary = "#f97316",
            PrimaryDark = "#ea580c",
            PrimaryDarker = "#c2410c",
            PrimaryLight = "#fb923c",
            PrimaryTint = "#ffedd5",
            Accent = "#facc15",
            AccentDark = "#eab308",
            Ink = "#3f2d20",
            InkMuted = "#7c5a43",
            InkSubtle = "#a1887f",
            Surface = "#fffbf5",
            SurfaceMuted = "#fff4e8",
            SurfaceRaised = "#ffead6",
            Border = "#f3d9bf",
            BorderStrong = "#e7c39c",
            Danger = "#dc2626",
            Success = "#16a34a",
            Radius = 14,
            Shadow = "0 6px 18px rgba(217,119,6,.16)",
            FontStack = "'Poppins','Segoe UI',Roboto,Arial,sans-serif"
        },

        new()
        {
            Key = "boutique",
            Name = "Boutique",
            Description = "Plum and antique gold with a serif face. Elegant, editorial, high-end.",
            Tone = "Elegant",
            Primary = "#8b3a62",
            PrimaryDark = "#6d2c4c",
            PrimaryDarker = "#571f3b",
            PrimaryLight = "#a85c80",
            PrimaryTint = "#f6e3ec",
            Accent = "#d4af37",
            AccentDark = "#b8941f",
            Ink = "#2b1b22",
            InkMuted = "#5c4450",
            InkSubtle = "#8a7381",
            Surface = "#fffdfd",
            SurfaceMuted = "#faf5f7",
            SurfaceRaised = "#f4eaef",
            Border = "#ecd9e2",
            BorderStrong = "#dcc2ce",
            Danger = "#b91c1c",
            Success = "#15803d",
            Radius = 2,
            Shadow = "0 2px 10px rgba(139,58,98,.14)",
            FontStack = "Georgia,'Times New Roman',Cambria,serif"
        },

        new()
        {
            Key = "forest",
            Name = "Forest",
            Description = "Deep greens and ochre on soft sage. Natural, calm and sustainable-feeling.",
            Tone = "Natural",
            Primary = "#15803d",
            PrimaryDark = "#166534",
            PrimaryDarker = "#14532d",
            PrimaryLight = "#22c55e",
            PrimaryTint = "#dcfce7",
            Accent = "#eab308",
            AccentDark = "#ca8a04",
            Ink = "#1c2b22",
            InkMuted = "#42584c",
            InkSubtle = "#7b8f83",
            Surface = "#ffffff",
            SurfaceMuted = "#f5faf6",
            SurfaceRaised = "#eaf4ec",
            Border = "#d4e6d9",
            BorderStrong = "#b9d4c1",
            Danger = "#b91c1c",
            Success = "#15803d",
            Radius = 8,
            Shadow = "0 4px 14px rgba(21,128,61,.14)",
            FontStack = "'Nunito Sans','Segoe UI',Roboto,Arial,sans-serif"
        },

        new()
        {
            Key = "mono",
            Name = "Mono",
            Description = "Black, white and grey with square corners. Minimal and product-led.",
            Tone = "Minimal",
            Primary = "#111827",
            PrimaryDark = "#000000",
            PrimaryDarker = "#000000",
            PrimaryLight = "#374151",
            PrimaryTint = "#e5e7eb",
            Accent = "#e5e7eb",
            AccentDark = "#d1d5db",
            Ink = "#111827",
            InkMuted = "#374151",
            InkSubtle = "#6b7280",
            Surface = "#ffffff",
            SurfaceMuted = "#fafafa",
            SurfaceRaised = "#f4f4f5",
            Border = "#e4e4e7",
            BorderStrong = "#d4d4d8",
            Danger = "#dc2626",
            Success = "#15803d",
            Radius = 0,
            Shadow = "0 1px 0 rgba(0,0,0,.06)",
            FontStack = "'Helvetica Neue',Helvetica,Arial,sans-serif"
        }
    }.AsReadOnly();

    private static readonly Dictionary<string, ShopTheme> ByKey =
        Themes.ToDictionary(t => t.Key, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ShopTheme> All => Themes;

    public ShopTheme Default => ByKey[DefaultKey];

    public ShopTheme Get(string? key) =>
        !string.IsNullOrWhiteSpace(key) && ByKey.TryGetValue(key, out var theme) ? theme : Default;

    public bool Contains(string? key) =>
        !string.IsNullOrWhiteSpace(key) && ByKey.ContainsKey(key);
}
