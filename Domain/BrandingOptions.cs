namespace WebDS_Test.Domain;

/// <summary>
/// Visual identity for the storefront, bound from the <c>Branding</c> configuration section.
/// </summary>
/// <remarks>
/// <para>
/// The default values reproduce the LibanPost web identity, read from the site's own stylesheet
/// (<c>css-EN.css</c>) rather than approximated. The two dominant brand colours are used
/// extensively across their CSS and are therefore high-confidence:
/// </para>
/// <list type="bullet">
/// <item><description>Primary blue <c>#006db7</c> — 91 occurrences; used for links, the top bar, active states, iconography.</description></item>
/// <item><description>Accent yellow <c>#fed300</c> — 34 occurrences; used for buttons and link-hover, with black text.</description></item>
/// </list>
/// <para>
/// The remaining values are derived from those or taken from the same stylesheet's greys
/// (<c>#f1f1f1</c> header, <c>#f6f6f6</c> and <c>#efefef</c> surfaces, <c>#015a97</c> footer,
/// <c>#2a4c62</c> muted text) and are safe to adjust without touching any markup.
/// </para>
/// <para>
/// Typography note: the live site uses licensed Gotham faces, which cannot be redistributed here.
/// <see cref="FontStack"/> therefore declares a free, metric-compatible-feeling geometric stack.
/// Drop the licensed webfonts into <c>wwwroot/lib/fonts</c> and add an <c>@font-face</c> block to
/// <c>brand.css</c> if exact type matching is required.
/// </para>
/// </remarks>
public sealed class BrandingOptions
{
    public const string SectionName = "Branding";

    /// <summary>Wordmark shown when no logo image is supplied.</summary>
    public string BrandName { get; set; } = "LibanPost";

    /// <summary>Optional logo path under <c>wwwroot</c>. When empty, a text wordmark is rendered.</summary>
    public string? LogoUrl { get; set; }

    /// <summary>Height of the logo image in pixels.</summary>
    public int LogoHeight { get; set; } = 52;

    public string Primary { get; set; } = "#006db7";
    public string PrimaryDark { get; set; } = "#015a97";
    public string PrimaryDarker { get; set; } = "#0b5f98";
    public string PrimaryLight { get; set; } = "#2683c2";
    public string PrimaryTint { get; set; } = "#c9e0f0";

    public string Accent { get; set; } = "#fed300";
    public string AccentDark { get; set; } = "#e0b900";

    public string Ink { get; set; } = "#1a1a1a";
    public string InkMuted { get; set; } = "#2a4c62";
    public string InkSubtle { get; set; } = "#6b6b6b";

    public string Surface { get; set; } = "#ffffff";
    public string SurfaceMuted { get; set; } = "#f6f6f6";
    public string SurfaceRaised { get; set; } = "#f1f1f1";
    public string Border { get; set; } = "#e6e6e6";
    public string BorderStrong { get; set; } = "#d7d7d7";

    public string Danger { get; set; } = "#e33c3c";
    public string Success { get; set; } = "#2e7d32";

    /// <summary>Corner radius. The brand's own scale is tight: 2–4px almost everywhere.</summary>
    public int Radius { get; set; } = 4;

    /// <summary>Card shadow, matching the brand's flat, low-elevation surfaces.</summary>
    public string Shadow { get; set; } = "0 1px 3px rgba(0,0,0,.10)";

    public string FontStack { get; set; } =
        "'Libre Franklin','Open Sans','Segoe UI',Roboto,'Helvetica Neue',Arial,sans-serif";

    /// <summary>
    /// Renders the palette as CSS custom properties so the stylesheet stays declarative and the
    /// values can be changed from configuration alone.
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
