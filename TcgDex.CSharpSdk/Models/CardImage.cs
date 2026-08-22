namespace TcgDex.Models;

/// <summary>Image resolutions the asset server offers.</summary>
public enum ImageQuality
{
    /// <summary>Full resolution.</summary>
    High,

    /// <summary>Reduced resolution, for thumbnails and lists.</summary>
    Low,
}

/// <summary>Image formats the asset server offers.</summary>
public enum ImageFormat
{
    /// <summary>PNG. Lossless, with transparency.</summary>
    Png,

    /// <summary>WebP. Smaller than PNG at comparable quality.</summary>
    Webp,

    /// <summary>JPEG. Smallest, no transparency.</summary>
    Jpg,
}

/// <summary>
/// Builds asset URLs from the base URLs the API returns.
/// </summary>
/// <remarks>
/// <see cref="Card.Image"/>, <see cref="SetBrief.Logo"/> and
/// <see cref="SetBrief.Symbol"/> are base URLs <em>without a file extension</em>;
/// requesting one directly returns 404. The quality and format have to be
/// appended, and these methods do it without the caller hand-assembling strings
/// or guessing at the accepted values.
/// </remarks>
public static class ImageUrl
{
    /// <summary>
    /// Builds a card artwork URL at a given quality and format.
    /// </summary>
    /// <param name="baseUrl">The base URL from the API, without an extension.</param>
    /// <param name="quality">The resolution to request.</param>
    /// <param name="format">The file format to request.</param>
    /// <returns>
    /// The full asset URL, or <see langword="null"/> when
    /// <paramref name="baseUrl"/> is null or blank — some cards genuinely have
    /// no artwork on record.
    /// </returns>
    /// <remarks>
    /// Card artwork is addressed as <c>{base}/{quality}.{format}</c>. Set logos
    /// and symbols are <em>not</em> — see <see cref="BuildAsset"/>.
    /// </remarks>
    public static string? Build(
        string? baseUrl,
        ImageQuality quality = ImageQuality.High,
        ImageFormat format = ImageFormat.Png)
    {
        // `is null ||` rather than IsNullOrWhiteSpace alone: the netstandard2.0
        // reference assembly does not annotate that method with
        // [NotNullWhen(false)], so without the explicit test the compiler still
        // considers baseUrl possibly-null below. Spelling it out keeps the
        // check honest on every target instead of silencing it with `!`.
        if (baseUrl is null || string.IsNullOrWhiteSpace(baseUrl))
        {
            return null;
        }

        // Tolerates a trailing slash so a caller who has already normalised the
        // value does not end up with a doubled separator.
        string trimmed = baseUrl.TrimEnd('/');

        return $"{trimmed}/{Name(quality)}.{Name(format)}";
    }

    /// <summary>
    /// Builds a set logo or symbol URL.
    /// </summary>
    /// <param name="baseUrl">The base URL from the API, without an extension.</param>
    /// <param name="format">The file format to request.</param>
    /// <returns>The full asset URL, or <see langword="null"/> when there is no base URL.</returns>
    /// <remarks>
    /// Logos and symbols are addressed as <c>{base}.{format}</c> — with **no**
    /// quality segment. Applying the card pattern to them returns 404, which is
    /// an easy mistake to make since all three fields look alike on the model.
    /// </remarks>
    public static string? BuildAsset(string? baseUrl, ImageFormat format = ImageFormat.Png)
    {
        if (baseUrl is null || string.IsNullOrWhiteSpace(baseUrl))
        {
            return null;
        }

        return $"{baseUrl.TrimEnd('/')}.{Name(format)}";
    }

    private static string Name(ImageQuality quality)
        => quality switch
        {
            ImageQuality.High => "high",
            ImageQuality.Low => "low",
            _ => throw new ArgumentOutOfRangeException(nameof(quality), quality, "Unknown image quality."),
        };

    private static string Name(ImageFormat format)
        => format switch
        {
            ImageFormat.Png => "png",
            ImageFormat.Webp => "webp",
            ImageFormat.Jpg => "jpg",
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown image format."),
        };
}

/// <summary>
/// Image URL helpers for the types that carry artwork.
/// </summary>
public static class ImageExtensions
{
    /// <summary>Builds the URL for this card's artwork.</summary>
    /// <param name="card">The card.</param>
    /// <param name="quality">The resolution to request.</param>
    /// <param name="format">The file format to request.</param>
    /// <returns>The image URL, or <see langword="null"/> when the card has no artwork.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="card"/> is null.</exception>
    public static string? GetImageUrl(
        this Card card,
        ImageQuality quality = ImageQuality.High,
        ImageFormat format = ImageFormat.Png)
    {
        Guard.NotNull(card);

        return ImageUrl.Build(card.Image, quality, format);
    }

    /// <summary>Builds the URL for this card's artwork.</summary>
    /// <param name="card">The card brief.</param>
    /// <param name="quality">The resolution to request.</param>
    /// <param name="format">The file format to request.</param>
    /// <returns>The image URL, or <see langword="null"/> when the card has no artwork.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="card"/> is null.</exception>
    public static string? GetImageUrl(
        this CardBrief card,
        ImageQuality quality = ImageQuality.High,
        ImageFormat format = ImageFormat.Png)
    {
        Guard.NotNull(card);

        return ImageUrl.Build(card.Image, quality, format);
    }

    /// <summary>Builds the URL for this set's logo.</summary>
    /// <param name="set">The set.</param>
    /// <param name="format">The file format to request.</param>
    /// <returns>The logo URL, or <see langword="null"/> when the set has none.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="set"/> is null.</exception>
    /// <remarks>
    /// Takes no quality: logos are served at a single resolution, unlike card
    /// artwork.
    /// </remarks>
    public static string? GetLogoUrl(this SetBrief set, ImageFormat format = ImageFormat.Png)
    {
        Guard.NotNull(set);

        return ImageUrl.BuildAsset(set.Logo, format);
    }

    /// <summary>Builds the URL for this set's logo.</summary>
    /// <param name="set">The set.</param>
    /// <param name="format">The file format to request.</param>
    /// <returns>The logo URL, or <see langword="null"/> when the set has none.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="set"/> is null.</exception>
    public static string? GetLogoUrl(this Set set, ImageFormat format = ImageFormat.Png)
    {
        Guard.NotNull(set);

        return ImageUrl.BuildAsset(set.Logo, format);
    }

    /// <summary>Builds the URL for this set's symbol.</summary>
    /// <param name="set">The set.</param>
    /// <param name="format">The file format to request.</param>
    /// <returns>The symbol URL, or <see langword="null"/> when the set has none.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="set"/> is null.</exception>
    /// <remarks>
    /// Takes no quality, and is served language-neutral rather than from the
    /// requested language's path.
    /// </remarks>
    public static string? GetSymbolUrl(this SetBrief set, ImageFormat format = ImageFormat.Png)
    {
        Guard.NotNull(set);

        return ImageUrl.BuildAsset(set.Symbol, format);
    }

    /// <summary>Builds the URL for this set's symbol.</summary>
    /// <param name="set">The set.</param>
    /// <param name="format">The file format to request.</param>
    /// <returns>The symbol URL, or <see langword="null"/> when the set has none.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="set"/> is null.</exception>
    public static string? GetSymbolUrl(this Set set, ImageFormat format = ImageFormat.Png)
    {
        Guard.NotNull(set);

        return ImageUrl.BuildAsset(set.Symbol, format);
    }
}
