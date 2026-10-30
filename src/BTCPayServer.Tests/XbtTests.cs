using System;
using BTCPayServer.Plugins.Xbt;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BTCPayServer.Tests;

public class XbtTests
{
    [Theory]
    [InlineData("XBT_LN", "XBT-LN")]
    [InlineData("XBT_CHAIN", "XBT-CHAIN")]
    [InlineData("XBT-LightningLike", "XBT-LN")]
    [InlineData("XBT", "XBT-CHAIN")]
    [InlineData("XBT-LN", "XBT-LN")]
    [InlineData("BTC_LN", "BTC-LN")]
    public void LegacyPaymentMethodIdsRemainNetworkSpecific(string input, string expected)
    {
        Assert.Equal(expected, Payments.PaymentMethodId.Parse(input).ToString());
        Assert.Equal(Payments.PaymentMethodId.Parse(expected), Payments.PaymentMethodId.Parse(input));
    }

    [Fact]
    public void MissingXbtNetworkNeverFallsBackToBtc()
    {
        var bitcoin = new BTCPayNetwork { CryptoCode = "BTC" };
        var provider = new BTCPayNetworkProvider(new[] { bitcoin },
            new NBXplorer.NBXplorerNetworkProvider(NBitcoin.ChainName.Mainnet), new BTCPayServer.Logging.Logs());
        Assert.Same(bitcoin, provider.GetNetwork("BTC"));
        Assert.Null(provider.GetNetwork("XBT"));
        Assert.Null(provider.GetNetwork("xbt"));
        Assert.False(provider.Support("XBT"));
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void PaymentMethodsToggleIndependently(bool chain, bool lightning)
    {
        var blob = new Data.StoreBlob();
        var other = Payments.PaymentTypes.CHAIN.GetPaymentMethodId("BTC");
        blob.SetExcluded(other, true);
        new XbtSettings { OnChainEnabled = chain, LightningEnabled = lightning }.Apply(blob);
        var read = XbtSettings.From(blob);
        Assert.Equal(chain, read.OnChainEnabled);
        Assert.Equal(lightning, read.LightningEnabled);
        Assert.True(blob.IsExcluded(other));
    }
    [Fact]
    public async System.Threading.Tasks.Task CurrencyDropdownIncludesXbtSats()
    {
        var table = new Services.Rates.CurrencyNameTable(new Services.Rates.CurrencyDataProvider[] {
            new Services.Rates.AssemblyCurrencyDataProvider(typeof(Rating.BidAsk).Assembly, "BTCPayServer.Rating.Currencies.json"),
            new Services.Rates.InMemoryCurrencyDataProvider(new[] {
                new Services.Rates.CurrencyData { Code = "XBT", Name = "Bitcoin BLAKE2b", Crypto = true },
                new Services.Rates.CurrencyData { Code = "BTCB2", Name = "Bitcoin BLAKE2b (XBT)", Crypto = true },
                new Services.Rates.CurrencyData { Code = "XBTSATS", Name = "XBT sats", Crypto = true }
            })
        }, Microsoft.Extensions.Logging.Abstractions.NullLogger<Services.Rates.CurrencyNameTable>.Instance);
        await table.ReloadCurrencyData(default);
        var context = new Microsoft.AspNetCore.Razor.TagHelpers.TagHelperContext(
            new Microsoft.AspNetCore.Razor.TagHelpers.TagHelperAttributeList(),
            new System.Collections.Generic.Dictionary<object, object>(), "test");
        var output = new Microsoft.AspNetCore.Razor.TagHelpers.TagHelperOutput("input",
            new Microsoft.AspNetCore.Razor.TagHelpers.TagHelperAttributeList(),
            (cache, encoder) => System.Threading.Tasks.Task.FromResult<Microsoft.AspNetCore.Razor.TagHelpers.TagHelperContent>(
                new Microsoft.AspNetCore.Razor.TagHelpers.DefaultTagHelperContent()));
        new TagHelpers.CurrenciesSuggestionsTagHelper(table).Process(context, output);
        var html = output.PostElement.GetContent();
        Assert.Contains("value=\"XBTSATS\">XBTSATS - XBT sats</option>", html);
        Assert.Contains("value=\"XBT\">", html);
        Assert.Contains("value=\"BTCB2\">", html);
        Assert.DoesNotContain("value=\"USDC\">", html);
    }

    [Fact]
    public void Btcb2IsExactlyOneXbtWithoutAnExchangeQuote()
    {
        var rules = Rating.RateRules.Parse(string.Join("\n", XbtPlugin.RateRules));
        foreach (var pair in new[] { "BTCB2_XBT", "XBT_BTCB2" })
        {
            var rule = rules.GetRuleFor(Rating.CurrencyPair.Parse(pair));
            Assert.True(rule.Reevaluate());
            Assert.Equal(1m, rule.BidAsk.Bid);
        }
        var sats = rules.GetRuleFor(new Rating.CurrencyPair("BTCB2", "XBTSATS"));
        Assert.True(sats.Reevaluate());
        Assert.Equal(100000000m, sats.BidAsk.Bid);
    }
    [Fact]
    public void XbtSatsConversionIsExactAndIndependentOfExchange()
    {
        var rules = Rating.RateRules.Parse(string.Join("\n", XbtPlugin.RateRules));
        var forward = rules.GetRuleFor(new Rating.CurrencyPair("XBT", XbtPlugin.SatsCurrency));
        Assert.True(forward.Reevaluate());
        Assert.Equal(100000000m, forward.BidAsk.Bid);
        var reverse = rules.GetRuleFor(new Rating.CurrencyPair(XbtPlugin.SatsCurrency, "XBT"));
        Assert.True(reverse.Reevaluate());
        Assert.Equal(0.00001m, 1000m * reverse.BidAsk.Bid);
        var btcSats = rules.GetRuleFor(new Rating.CurrencyPair("SATS", "BTC"));
        Assert.True(btcSats.Reevaluate());
        Assert.Equal(0.00000001m, btcSats.BidAsk.Bid);
    }

    [Fact]
    public void UsdPricingUsesNeoxExUsdcAndKrakenWithoutBtc()
    {
        var rules = Rating.RateRules.Parse(string.Join("\n", XbtPlugin.RateRules));
        var rule = rules.GetRuleFor(new Rating.CurrencyPair("XBT", "USD"));
        rule.ExchangeRates.SetRate("neoxex", Rating.CurrencyPair.Parse("XBT_USDC"), new Rating.BidAsk(400m));
        rule.ExchangeRates.SetRate("kraken", Rating.CurrencyPair.Parse("USDC_USD"), new Rating.BidAsk(0.999m, 1.001m));
        Assert.True(rule.Reevaluate());
        Assert.Equal(399.6m, rule.BidAsk.Bid);
        Assert.Equal(400.4m, rule.BidAsk.Ask);
        Assert.DoesNotContain("BTC", rule.ToString());
    }

    [Fact]
    public void XbtBtcHasNoImplicitAliasRule()
    {
        var rules = Rating.RateRules.Parse(string.Join("\n", XbtPlugin.RateRules));
        var rule = rules.GetRuleFor(new Rating.CurrencyPair("XBT", "BTC"));
        Assert.False(rule.Reevaluate());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void UsdPricingRequiresBothExchangeQuotes(bool neoxex, bool kraken)
    {
        var rules = Rating.RateRules.Parse(string.Join("\n", XbtPlugin.RateRules));
        var rule = rules.GetRuleFor(new Rating.CurrencyPair("XBT", "USD"));
        if (neoxex)
            rule.ExchangeRates.SetRate("neoxex", Rating.CurrencyPair.Parse("XBT_USDC"), new Rating.BidAsk(400m));
        if (kraken)
            rule.ExchangeRates.SetRate("kraken", Rating.CurrencyPair.Parse("USDC_USD"), new Rating.BidAsk(1m));
        Assert.False(rule.Reevaluate());
    }

    static JObject Quote(DateTimeOffset now) => JObject.FromObject(new {
        success = true, pair = "BTCB2_USDC", ticker = new { lastPrice = 400m, computedAt = now.ToUnixTimeMilliseconds() }
    });
    [Fact]
    public void NeoxExPricesXbtInUsdc()
    {
        var now = DateTimeOffset.UtcNow;
        var rates = NeoxExRateProvider.Parse(Quote(now), now);
        Assert.Single(rates);
        Assert.Equal("XBT_USDC", rates[0].CurrencyPair.ToString());
    }
    [Fact]
    public void RejectsStaleAndWrongCurrencyQuotes()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Throws<FormatException>(() => NeoxExRateProvider.Parse(Quote(now.AddMinutes(-6)),now));
        Assert.Throws<FormatException>(() => NeoxExRateProvider.Parse(Quote(now.AddMinutes(2)),now));
        var quote = Quote(now); quote["pair"] = "BTC_USDC";
        Assert.Throws<FormatException>(() => NeoxExRateProvider.Parse(quote,now));
        quote = Quote(now); quote["success"] = false;
        Assert.Throws<FormatException>(() => NeoxExRateProvider.Parse(quote,now));
        quote = Quote(now); quote["ticker"]!["lastPrice"] = 0;
        Assert.Throws<FormatException>(() => NeoxExRateProvider.Parse(quote,now));
        quote = Quote(now); quote["ticker"]!["lastPrice"] = -1;
        Assert.Throws<FormatException>(() => NeoxExRateProvider.Parse(quote,now));
    }
    [Theory]
    [InlineData(null, XbtPlugin.DefaultIcon)]
    [InlineData("", XbtPlugin.DefaultIcon)]
    [InlineData("branding/xbt-icon.svg", "branding/xbt-icon.svg")]
    [InlineData("https://example.com/xbt.png", "https://example.com/xbt.png")]
    [InlineData("http://example.com/xbt.png", XbtPlugin.DefaultIcon)]
    [InlineData("../secrets.svg", XbtPlugin.DefaultIcon)]
    [InlineData("branding/../x.svg", XbtPlugin.DefaultIcon)]
    [InlineData("javascript:alert(1)", XbtPlugin.DefaultIcon)]
    [InlineData("branding/xbt-icon.exe", XbtPlugin.DefaultIcon)]
    public void IconIsConfigurableAndFallsBackSafely(string? configured, string expected)
    {
        Assert.Equal(expected, XbtPlugin.ResolveIcon(configured));
    }

    [Theory]
    [InlineData(null, XbtPlugin.DefaultSatsLabel)]
    [InlineData("  ", XbtPlugin.DefaultSatsLabel)]
    [InlineData("XBTC sats", "XBTC sats")]
    [InlineData(" sats XBT ", "sats XBT")]
    [InlineData("<b>sats</b>", XbtPlugin.DefaultSatsLabel)]
    [InlineData(" leading", "leading")]
    [InlineData("a label that is far too long", XbtPlugin.DefaultSatsLabel)]
    public void SatsLabelIsConfigurableAndFallsBackSafely(string? configured, string expected)
    {
        Assert.Equal(expected, XbtPlugin.ResolveSatsLabel(configured));
    }

    [Fact]
    public void Bolt12PaymentMethodIdIsXbtSpecific()
    {
        var pmi = Bolt12.GetPaymentMethodId("XBT");
        Assert.Equal("XBT-BOLT12", pmi.ToString());
        Assert.Equal(pmi, Payments.PaymentMethodId.Parse("XBT-BOLT12"));
        Assert.NotEqual(Payments.PaymentTypes.LN.GetPaymentMethodId("XBT"), pmi);
    }

    [Fact]
    public void Bolt12IsOptIn()
    {
        var store = new Data.StoreData();
        Assert.False(XbtSettings.From(store).Bolt12Enabled);
        new XbtSettings { OnChainEnabled = true, LightningEnabled = true, Bolt12Enabled = true }.Apply(store);
        Assert.True(XbtSettings.From(store).Bolt12Enabled);
        new XbtSettings { OnChainEnabled = true, LightningEnabled = true, Bolt12Enabled = false }.Apply(store);
        var read = XbtSettings.From(store);
        Assert.False(read.Bolt12Enabled);
        Assert.True(read.LightningEnabled);
    }

    [Fact]
    public void OffersClientReachesTheStoreLndClient()
    {
        // TryCreate reads two non-public fields of BTCPayServer.Lightning; fail loudly if an
        // update renames them rather than silently turning BOLT 12 off.
        Assert.True(LndOffersClient.IsSupported);
        var lnd = new Lightning.LND.LndClient(new Lightning.LND.LndRestSettings(new Uri("https://node.example:8080/"))
        {
            Macaroon = new byte[] { 1, 2, 3 }
        }, NBitcoin.Network.Main);
        Assert.NotNull(LndOffersClient.TryCreate(lnd));
    }

    sealed class FakeLnd : System.Net.Http.HttpMessageHandler
    {
        public readonly System.Collections.Generic.List<(string Path, string? Macaroon, JObject Body)> Requests = new();
        public required System.Func<string, string> Reply { get; init; }
        protected override async System.Threading.Tasks.Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
        {
            var body = request.Content is null ? new JObject() : JObject.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
            request.Headers.TryGetValues("Grpc-Metadata-macaroon", out var macaroon);
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add((path, macaroon is null ? null : string.Join(",", macaroon), body));
            return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent(Reply(path))
            };
        }
    }

    static LndOffersClient OffersClient(FakeLnd fake) => new(new System.Net.Http.HttpClient(fake), "https://node.example:8080/",
        new Lightning.LND.LndAuthentication.FixedMacaroonAuthentication(new byte[] { 0xab, 0xcd }));

    [Fact]
    public async System.Threading.Tasks.Task OffersClientCreatesSingleUseOffer()
    {
        var offerId = new byte[32]; offerId[0] = 0x5a;
        var fake = new FakeLnd
        {
            Reply = _ => new JObject
            {
                ["offer"] = new JObject { ["offer_id"] = Convert.ToBase64String(offerId), ["bolt12"] = "lno1abc" },
                ["created"] = true
            }.ToString()
        };
        var offer = await OffersClient(fake).CreateOffer(new Bolt12OfferRequest("Shop (invoice X1)", 1_500_000, 1_900_000_000, "Shop", "xbtpay:X1"), default);
        Assert.Equal("5a" + new string('0', 62), offer.OfferId);
        Assert.Equal("lno1abc", offer.Bolt12);
        Assert.True(offer.Created);
        var (path, macaroon, body) = Assert.Single(fake.Requests);
        Assert.Equal("/v2/offers", path);
        Assert.Equal("abcd", macaroon);
        Assert.Equal("1500000", body.Value<string>("amount_msat"));
        Assert.Equal("1900000000", body.Value<string>("absolute_expiry"));
        Assert.Equal("Shop (invoice X1)", body.Value<string>("description"));
    }

    [Fact]
    public async System.Threading.Tasks.Task OffersClientOnlyReturnsInvoicesOfTheAskedOffer()
    {
        var mine = new byte[32]; mine[0] = 1;
        var other = new byte[32]; other[0] = 2;
        var hash = new byte[32]; hash[31] = 7;
        var fake = new FakeLnd
        {
            Reply = _ => new JObject
            {
                ["invoices"] = new JArray(
                    new JObject { ["offer_id"] = Convert.ToBase64String(mine), ["payment_hash"] = Convert.ToBase64String(hash),
                        ["state"] = "SETTLED", ["amount_paid_msat"] = "1500000", ["created_at"] = "1800000000" },
                    new JObject { ["offer_id"] = Convert.ToBase64String(other), ["payment_hash"] = Convert.ToBase64String(hash),
                        ["state"] = "SETTLED", ["amount_paid_msat"] = "9999000", ["created_at"] = "1800000000" })
            }.ToString()
        };
        var mineHex = NBitcoin.DataEncoders.Encoders.Hex.EncodeData(mine);
        var paid = Assert.Single(await OffersClient(fake).ListOfferInvoices(mineHex, default));
        Assert.Equal("SETTLED", paid.State);
        Assert.Equal(1_500_000UL, paid.AmountPaidMsat);
        Assert.Equal(NBitcoin.DataEncoders.Encoders.Hex.EncodeData(hash), paid.PaymentHash);
        var request = Assert.Single(fake.Requests);
        Assert.Equal("/v2/offers/invoices", request.Path);
        Assert.Equal(Convert.ToBase64String(mine), request.Body.Value<string>("offer_id"));
    }

    [Fact]
    public async System.Threading.Tasks.Task OffersClientReadsProofAndPayerNote()
    {
        var preimage = new byte[32]; preimage[0] = 9;
        var fake = new FakeLnd
        {
            Reply = path => path.StartsWith("/v1/invoice/")
                ? new JObject { ["r_preimage"] = Convert.ToBase64String(preimage) }.ToString()
                : new JObject { ["type"] = "invoice", ["invoice_request"] = new JObject { ["payer_note"] = "Thanks!" } }.ToString()
        };
        var client = OffersClient(fake);
        Assert.Equal(NBitcoin.DataEncoders.Encoders.Hex.EncodeData(preimage), await client.GetPreimage("ab01", default));
        Assert.Equal("Thanks!", await client.GetPayerNote("lni1xyz", default));
        Assert.Equal("/v1/invoice/ab01", fake.Requests[0].Path);
        Assert.Equal("/v2/offers/decode", fake.Requests[1].Path);
        Assert.Equal("lni1xyz", fake.Requests[1].Body.Value<string>("bolt12"));
    }

    [Fact]
    public void Bolt12PaymentsShowAsLightningPayments()
    {
        // The invoice page lists a payment whose handler is a Lightning one, reading its
        // destination and proof from LightningPaymentData.
        var details = JObject.FromObject(new Bolt12PaymentDetails { PaymentHash = "aa", Preimage = "bb", Invoice = "lni1xyz", PayerNote = "hi" });
        var read = details.ToObject<Client.Models.LightningPaymentData>()!;
        Assert.Equal("aa", read.PaymentHash);
        Assert.Equal("bb", read.Preimage);
        Assert.Equal("lni1xyz", read.BOLT11);
        Assert.True(typeof(Payments.Lightning.ILightningPaymentHandler).IsAssignableFrom(typeof(Bolt12PaymentHandler)));
    }
}
