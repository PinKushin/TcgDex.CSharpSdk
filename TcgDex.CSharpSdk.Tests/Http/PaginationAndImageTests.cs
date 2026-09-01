namespace TcgDex.Tests.Http;

using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TcgDex;
using TcgDex.Models;
using TcgDex.Querying;

/// <summary>
/// Auto-pagination and image URL construction.
/// </summary>
[TestFixture]
public sealed class PaginationAndImageTests
{
    private static TcgDexClient CreateClient(RecordingHandler handler)
        => new(new HttpClient(handler), new TcgDexOptions());

    private static string Page(int count, int startAt = 0)
    {
        IEnumerable<string> cards = Enumerable.Range(startAt, count)
            .Select(i => $$"""{"id":"c-{{i}}","localId":"{{i}}","name":"Card {{i}}"}""");

        return $"[{string.Join(",", cards)}]";
    }

    // ----- streaming -----

    [Test]
    public async Task StreamAsync_FollowsPagesUntilAShortOneArrives()
    {
        RecordingHandler handler = new RecordingHandler()
            .RespondWith(HttpStatusCode.OK, Page(3, 0))
            .RespondWith(HttpStatusCode.OK, Page(3, 3))
            .RespondWith(HttpStatusCode.OK, Page(1, 6));

        List<CardBrief> cards = new();

        await foreach (CardBrief card in CreateClient(handler).Cards.StreamAsync(new CardQuery(), 3, CancellationToken.None))
        {
            cards.Add(card);
        }

        cards.Count.ShouldBe(7);
        cards[0].Id.ShouldBe("c-0");
        cards[6].Id.ShouldBe("c-6");
        handler.Requests.Count.ShouldBe(3);
    }

    [Test]
    public async Task StreamAsync_RequestsSequentialPages()
    {
        RecordingHandler handler = new RecordingHandler()
            .RespondWith(HttpStatusCode.OK, Page(2, 0))
            .RespondWith(HttpStatusCode.OK, Page(0));

        await foreach (CardBrief _ in CreateClient(handler).Cards.StreamAsync(new CardQuery(), 2, CancellationToken.None))
        {
        }

        handler.Requests[0].RequestUri!.ToString().ShouldContain("pagination:page=1");
        handler.Requests[1].RequestUri!.ToString().ShouldContain("pagination:page=2");
        handler.Requests.ShouldAllBe(r => r.RequestUri!.ToString().Contains("itemsPerPage=2"));
    }

    [Test]
    public async Task StreamAsync_WhenTheFinalPageIsExactlyFull_MakesOneMoreRequest()
    {
        // Unavoidable: with no total count, a full page is indistinguishable
        // from "there is more" until the next request comes back empty.
        RecordingHandler handler = new RecordingHandler()
            .RespondWith(HttpStatusCode.OK, Page(2, 0))
            .RespondWith(HttpStatusCode.OK, "[]");

        int count = 0;

        await foreach (CardBrief _ in CreateClient(handler).Cards.StreamAsync(new CardQuery(), 2, CancellationToken.None))
        {
            count++;
        }

        count.ShouldBe(2);
        handler.Requests.Count.ShouldBe(2);
    }

    [Test]
    public async Task StreamAsync_WithNoResults_YieldsNothing()
    {
        RecordingHandler handler = new RecordingHandler().RespondWith(HttpStatusCode.OK, "[]");

        int count = 0;

        await foreach (CardBrief _ in CreateClient(handler).Cards.StreamAsync(new CardQuery(), 10, CancellationToken.None))
        {
            count++;
        }

        count.ShouldBe(0);
        handler.Requests.Count.ShouldBe(1);
    }

    [Test]
    public async Task StreamAsync_StopsFetchingWhenTheConsumerBreaks()
    {
        // Laziness is the point: taking two results from a large set must not
        // download the whole thing.
        RecordingHandler handler = new RecordingHandler().RespondWith(HttpStatusCode.OK, Page(50, 0));

        List<CardBrief> taken = new();

        await foreach (CardBrief card in CreateClient(handler).Cards.StreamAsync(new CardQuery(), 50, CancellationToken.None))
        {
            taken.Add(card);

            if (taken.Count == 2)
            {
                break;
            }
        }

        taken.Count.ShouldBe(2);
        handler.Requests.Count.ShouldBe(1, "breaking early must not trigger another page");
    }

    [Test]
    public async Task StreamAsync_AppliesTheQueryFilters()
    {
        RecordingHandler handler = new RecordingHandler().RespondWith(HttpStatusCode.OK, "[]");

        CardQuery query = new CardQuery().Where(c => c.Name == "Furret").OrderBy(c => c.Name);

        await foreach (CardBrief _ in CreateClient(handler).Cards.StreamAsync(query, 10, CancellationToken.None))
        {
        }

        string uri = handler.Requests[0].RequestUri!.ToString();
        uri.ShouldContain("name=eq:Furret");
        uri.ShouldContain("sort:field=name");
    }

    [Test]
    public async Task StreamAsync_OverridesAnyPageAlreadyOnTheQuery()
    {
        // The stream owns paging; a page left on the query would silently skip
        // results or repeat them.
        RecordingHandler handler = new RecordingHandler().RespondWith(HttpStatusCode.OK, "[]");

        CardQuery query = new CardQuery().Page(7, 3);

        await foreach (CardBrief _ in CreateClient(handler).Cards.StreamAsync(query, 25, CancellationToken.None))
        {
        }

        string uri = handler.Requests[0].RequestUri!.ToString();
        uri.ShouldContain("pagination:page=1");
        uri.ShouldContain("itemsPerPage=25");
    }

    [Test]
    public async Task StreamAsync_WithNullQuery_Throws()
    {
        RecordingHandler handler = new();

        await Should.ThrowAsync<ArgumentNullException>(async () =>
        {
            await foreach (CardBrief _ in CreateClient(handler).Cards.StreamAsync(null!, 10, CancellationToken.None))
            {
            }
        });
    }

    [TestCase(0)]
    [TestCase(-1)]
    public async Task StreamAsync_WithInvalidPageSize_Throws(int pageSize)
    {
        RecordingHandler handler = new();

        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () =>
        {
            await foreach (CardBrief _ in CreateClient(handler).Cards.StreamAsync(new CardQuery(), pageSize, CancellationToken.None))
            {
            }
        });
    }

    [Test]
    public async Task StreamAsync_WhenCancelled_StopsEnumerating()
    {
        RecordingHandler handler = new RecordingHandler().RespondWith(HttpStatusCode.OK, Page(5, 0));

        using CancellationTokenSource cts = new();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await foreach (CardBrief _ in CreateClient(handler).Cards.StreamAsync(new CardQuery(), 5, cts.Token))
            {
            }
        });
    }

    // ----- image URLs -----

    [TestCase(ImageQuality.High, ImageFormat.Png, "https://assets.tcgdex.net/en/swsh/swsh3/136/high.png")]
    [TestCase(ImageQuality.Low, ImageFormat.Webp, "https://assets.tcgdex.net/en/swsh/swsh3/136/low.webp")]
    [TestCase(ImageQuality.High, ImageFormat.Jpg, "https://assets.tcgdex.net/en/swsh/swsh3/136/high.jpg")]
    public void ImageUrl_AppendsQualityAndFormat(ImageQuality quality, ImageFormat format, string expected)
        => ImageUrl.Build("https://assets.tcgdex.net/en/swsh/swsh3/136", quality, format).ShouldBe(expected);

    [Test]
    public void ImageUrl_DefaultsToHighPng()
        => ImageUrl.Build("https://assets.tcgdex.net/en/swsh/swsh3/136")
            .ShouldBe("https://assets.tcgdex.net/en/swsh/swsh3/136/high.png");

    [Test]
    public void ImageUrl_ToleratesATrailingSlash()
        => ImageUrl.Build("https://assets.tcgdex.net/en/swsh/swsh3/136/")
            .ShouldBe("https://assets.tcgdex.net/en/swsh/swsh3/136/high.png");

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void ImageUrl_WithNoBase_ReturnsNull(string? baseUrl)
        => ImageUrl.Build(baseUrl).ShouldBeNull();

    [Test]
    public void GetImageUrl_OnACardWithoutArtwork_ReturnsNull()
    {
        // `exu-!` is a real card with no image.
        Card card = Fixture.Load<Card>("card-missing-image.json");

        card.GetImageUrl().ShouldBeNull();
    }

    [Test]
    public void GetImageUrl_OnACardWithArtwork_BuildsTheUrl()
    {
        Card card = Fixture.Load<Card>("card-pokemon-full.json");

        card.GetImageUrl(ImageQuality.Low, ImageFormat.Webp).ShouldEndWith("/low.webp");
    }

    [Test]
    public void GetLogoAndSymbolUrls_AreBuiltFromTheSet()
    {
        Card card = Fixture.Load<Card>("card-pokemon-full.json");

        // Logos and symbols take no quality segment — `{base}.{format}`.
        // Applying the card pattern to them returns 404.
        card.Set.GetLogoUrl().ShouldEndWith("/logo.png");
        card.Set.GetSymbolUrl(ImageFormat.Webp).ShouldEndWith("/symbol.webp");
    }

    [Test]
    public void GetImageUrl_OnABrief_BuildsTheUrl()
    {
        IReadOnlyList<CardBrief> briefs = Fixture.Load<IReadOnlyList<CardBrief>>("list-cards-brief.json");
        CardBrief withImage = briefs.First(b => b.Image is not null);

        withImage.GetImageUrl().ShouldEndWith("/high.png");
    }

    [Test]
    public void ImageHelpers_RejectNullReceivers()
    {
        Should.Throw<ArgumentNullException>(() => ((Card)null!).GetImageUrl());
        Should.Throw<ArgumentNullException>(() => ((CardBrief)null!).GetImageUrl());
        Should.Throw<ArgumentNullException>(() => ((SetBrief)null!).GetLogoUrl());
        Should.Throw<ArgumentNullException>(() => ((SetBrief)null!).GetSymbolUrl());
        Should.Throw<ArgumentNullException>(() => ((Set)null!).GetLogoUrl());
        Should.Throw<ArgumentNullException>(() => ((Set)null!).GetSymbolUrl());
    }

    [Test]
    public void GetLogoAndSymbolUrls_WorkOnAFullSetToo()
    {
        // Both Set and SetBrief carry logos, and a caller holding either should
        // not have to know which overload they are on.
        Set set = Fixture.Load<Set>("set-full.json");

        set.GetLogoUrl().ShouldEndWith("/logo.png");
        set.GetSymbolUrl(ImageFormat.Webp).ShouldEndWith("/symbol.webp");
    }

    [Test]
    public void BuildAsset_OmitsTheQualitySegment()
    {
        // The distinction that matters: set assets are addressed differently
        // from card artwork.
        ImageUrl.BuildAsset("https://assets.tcgdex.net/en/swsh/swsh3/logo")
            .ShouldBe("https://assets.tcgdex.net/en/swsh/swsh3/logo.png");

        ImageUrl.Build("https://assets.tcgdex.net/en/swsh/swsh3/136")
            .ShouldBe("https://assets.tcgdex.net/en/swsh/swsh3/136/high.png");
    }

    [TestCase(null)]
    [TestCase("")]
    public void BuildAsset_WithNoBase_ReturnsNull(string? baseUrl)
        => ImageUrl.BuildAsset(baseUrl).ShouldBeNull();

    [Test]
    public void BuildAsset_WithAnUnknownFormat_Throws()
        => Should.Throw<ArgumentOutOfRangeException>(
            () => ImageUrl.BuildAsset("https://x", (ImageFormat)99));

    [Test]
    public void ImageUrl_WithAnUnknownEnumValue_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(
            () => ImageUrl.Build("https://x", (ImageQuality)99));
        Should.Throw<ArgumentOutOfRangeException>(
            () => ImageUrl.Build("https://x", ImageQuality.High, (ImageFormat)99));
    }
}
