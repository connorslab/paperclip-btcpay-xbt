using System;
using BTCPayServer.Plugins.Xbt;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BTCPayServer.Tests;

public class XbtTests
{
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
}
