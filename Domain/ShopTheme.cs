namespace WebDS_Test.Domain;

/// <summary>
/// A selectable storefront template theme for a vendor's shop.
/// </summary>
/// <remarks>
/// <para>
/// A theme is deliberately data, not code: it carries the same palette tokens the marketplace
/// already exposes as CSS custom properties (<c>--brand-*</c>, see
/// <see cref="BrandingOptions.ToCssVariables"/>), plus a font stack, corner radius and shadow.
/// Applying a theme is therefore just writing a different block of variables onto the page — no
/// markup changes, and every existing component (cards, buttons, hero, nav, footer) re-skins
/// itself for free.
/// </para>
/// <para>
/// Themes are catalogued in <c>IThemeCatalog</c>, which is what both the vendor picker and the
/// storefront resolve against, so an unknown key can never leak into rendering.
/// </para>
/// </remarks>
public sealed record ShopTheme
{
    /// <summary>Stable key stored on <see cref="Shop.ThemeKey"/>. Never localised.</summary>
    public required string Key { get; init; }

    /// <summary>Display name shown in the vendor picker.</summary>
    public required string Name { get; init; }

    /// <summary>One-line description of the mood, shown under the name.</summary>
    public required string Description { get; init; }

    /// <summary>Short mood word ("Dark", "Warm", …) used as a badge.</summary>
    public required string Tone { get; init; }

    // ---- Palette (mirrors BrandingOptions) -----------------------------
    public required string Primary { get; init; }
    public required string PrimaryDark { get; init; }
    public required string PrimaryDarker { get; init; }
    public required string PrimaryLight { get; init; }
    public required string PrimaryTint { get; init; }

    public required string Accent { get; init; }
    public required string AccentDark { get; init; }

    public required string Ink { get; init; }
    public required string InkMuted { get; init; }
    public required string InkSubtle { get; init; }

    public required string Surface { get; init; }
    public required string SurfaceMuted { get; init; }
    public required string SurfaceRaised { get; init; }

    public required string Border { get; init; }
    public required string BorderStrong { get; init; }

    public required string Danger { get; init; }
    public required string Success { get; init; }

    // ---- Shape and type -------------------------------------------------
    public required int Radius { get; init; }
    public required string Shadow { get; init; }
    public required string FontStack { get; init; }

    /// <summary>True when the base surface is dark, so chrome can choose light ink.</summary>
    public bool IsDark { get; init; }

    /// <summary>Three colours used to preview the theme in the picker.</summary>
    public IReadOnlyList<string> Swatch => new[] { Primary, Accent, Surface };

    /// <summary>
    /// Renders the palette as the CSS custom properties the storefront reads. Emitted inline on the
    /// page (or on a preview card), which is enough to override the <c>:root</c> defaults because an
    /// inline style beats a stylesheet rule.
    /// </summary>
    public string ToCssVariables()
    {
        var variables = new (string Name, string Value)[]
        {
            ("--brand-primary", Primary),
            ("--brand-primary-dark", PrimaryDark),
            ("--brand-primary-darker", PrimaryDarker),
            ("--brand-primary-light", PrimaryLight),
            ("--brand-primary-tint", PrimaryTint),
            ("--brand-accent", Accent),
            ("--brand-accent-dark", AccentDark),
            ("--brand-ink", Ink),
            ("--brand-ink-muted", InkMuted),
            ("--brand-ink-subtle", InkSubtle),
            ("--brand-surface", Surface),
            ("--brand-surface-muted", SurfaceMuted),
            ("--brand-surface-raised", SurfaceRaised),
            ("--brand-border", Border),
            ("--brand-border-strong", BorderStrong),
            ("--brand-danger", Danger),
            ("--brand-success", Success),
            ("--brand-radius", $"{Radius}px"),
            ("--brand-shadow", Shadow),
            ("--brand-font", FontStack)
        };

        return string.Join(";", variables.Select(v => $"{v.Name}:{v.Value}"));
    }
}
