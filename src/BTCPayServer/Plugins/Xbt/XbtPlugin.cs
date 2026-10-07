using System;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Hosting;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Payments.Bitcoin;
using BTCPayServer.Services;
using BTCPayServer.Services.Rates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Text.RegularExpressions;
using NBXplorer;

namespace BTCPayServer.Plugins.Xbt;

public sealed class XbtPaymentNetwork : BTCPayNetwork { }

public sealed class XbtPlugin : BaseBTCPayServerPlugin
{
    public const string SatsCurrency = "XBTSATS";
    public static readonly string[] RateRules = {
        "XBT_BTCB2 = 1;",
        "BTCB2_XBT = 1;",
        "BTCB2_X = BTCB2_XBT * XBT_X;",
        "XBT_XBTSATS = 100000000;",
        "XBTSATS_X = XBTSATS_XBT * XBT_X;",
        "XBT_X = neoxex(XBT_X);"
    };
    public const string DefaultIcon = "imlegacy/xbtpay.svg";

    /// <summary>
    /// The XBT payment icon shown in checkout and in the middle of the QR codes. Operators
    /// can set BTCPAY_XBTICON to an image under wwwroot (e.g. a mounted file) or an https
    /// URL. Anything else falls back to the default, so a typo never breaks checkout.
    /// </summary>
    public static string ResolveIcon(string? configured)
    {
        var value = configured?.Trim();
        if (string.IsNullOrEmpty(value)) return DefaultIcon;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return uri.Scheme == Uri.UriSchemeHttps ? uri.AbsoluteUri : DefaultIcon;
        return Regex.IsMatch(value, "^[A-Za-z0-9_-]+(/[A-Za-z0-9_.-]+)*\\.(svg|png|jpg|jpeg|webp)$") && !value.Contains("..")
            ? value : DefaultIcon;
    }

    public const string DefaultSatsLabel = "XBT sats";

    /// <summary>
    /// The label shown next to XBTSATS amounts, e.g. in Point of Sale. Operators can set
    /// BTCPAY_XBTSATSLABEL to name the unit their way. Only short plain text is accepted;
    /// anything else falls back to the default.
    /// </summary>
    public static string ResolveSatsLabel(string? configured)
    {
        var value = configured?.Trim();
        return !string.IsNullOrEmpty(value) && Regex.IsMatch(value, "^[\\p{L}\\p{N}][\\p{L}\\p{N} ]{0,23}$")
            ? value : DefaultSatsLabel;
    }

    // Stable identity retained for existing installations and plugin dependencies.
    public override string Identifier => "Paperclip.XbtLightning";
    public override string Name => "XBTPay (beta)";
    public override string Description => "XBTPay: Bitcoin on-chain and Lightning checkout. Not independently audited.";

    public override void Execute(IServiceCollection services)
    {
        var bootstrap = ((PluginServiceCollection)services).BootstrapServices;
        if (!bootstrap.GetRequiredService<SelectedChains>().Contains("XBT")) return;
        var nbx = bootstrap.GetRequiredService<NBXplorerNetworkProvider>().GetFromCryptoCode("XBT");
        var config = bootstrap.GetService<IConfiguration>();
        var icon = ResolveIcon(config?["XBTICON"]);
        var satsLabel = ResolveSatsLabel(config?["XBTSATSLABEL"]);
        var network = new XbtPaymentNetwork
        {
            CryptoCode = "XBT", DisplayName = "Bitcoin BLAKE2b",
            NBXplorerNetwork = nbx,
            CryptoImagePath = icon, LightningImagePath = icon,
            DefaultSettings = BTCPayDefaultSettings.GetDefaultSettings(nbx.NBitcoinNetwork.ChainName),
            WalletSupported = true, ReadonlyWallet = true, SupportLightning = true, ShowSyncSummary = true,
            CoinType = nbx.CoinType, SupportPayJoin = false, SupportRBF = false, VaultSupported = false,
            DefaultRateRules = RateRules
        };
        network.SetDefaultElectrumMapping(nbx.NBitcoinNetwork.ChainName);
        services.AddBTCPayNetwork((BTCPayNetworkBase)network);
        services.AddUIExtension("store-integrations-nav", "/Plugins/Xbt/Views/Nav.cshtml");
        var onchain = PaymentTypes.CHAIN.GetPaymentMethodId("XBT");
        services.AddDefaultPrettyName(onchain, "XBT on-chain (BLAKE2b)");
        services.AddSingleton<IPaymentMethodHandler>(p => ActivatorUtilities.CreateInstance<BitcoinLikePaymentHandler>(p, network, onchain));
        services.AddSingleton<IPaymentLinkExtension>(p => ActivatorUtilities.CreateInstance<BitcoinPaymentLinkExtension>(p, network, onchain));
        services.AddSingleton<ICheckoutModelExtension>(p => ActivatorUtilities.CreateInstance<BitcoinCheckoutModelExtension>(p, network, onchain));
        services.AddSingleton<IPaymentMethodBitpayAPIExtension>(p => ActivatorUtilities.CreateInstance<BitcoinPaymentMethodBitpayAPIExtension>(p, onchain));
        services.AddTransactionLinkProvider(onchain, new DefaultTransactionLinkProvider("https://mempool.guide/tx/{0}"));
        services.AddCurrencyData(new CurrencyData { Code = "XBT", Name = "Bitcoin BLAKE2b", Divisibility = 8, Crypto = true, Symbol = "XBT" });
        services.AddCurrencyData(new CurrencyData { Code = "BTCB2", Name = "Bitcoin BLAKE2b (XBT)", Divisibility = 8, Crypto = true, Symbol = "BTCB2" });
        services.AddCurrencyData(new CurrencyData { Code = SatsCurrency, Name = satsLabel, Divisibility = 0, Crypto = true, Symbol = satsLabel });
        services.AddCurrencyData(new CurrencyData { Code = "USDC", Name = "USD Coin (pricing only)", Divisibility = 6, Crypto = true, Symbol = "USDC" });
        services.AddRateProvider<NeoxExRateProvider>();
        var pmi = PaymentTypes.LN.GetPaymentMethodId("XBT");
        services.AddDefaultPrettyName(pmi, "XBT Lightning (BLAKE2b)");
        services.AddSingleton<IPaymentMethodHandler>(p => ActivatorUtilities.CreateInstance<LightningLikePaymentHandler>(p, network, pmi));
        services.AddSingleton<IPaymentLinkExtension>(p => ActivatorUtilities.CreateInstance<LightningPaymentLinkExtension>(p, network, pmi));
        services.AddSingleton<ICheckoutModelExtension>(p => ActivatorUtilities.CreateInstance<LNCheckoutModelExtension>(p, network, pmi));
        services.AddSingleton<IPaymentMethodBitpayAPIExtension>(p => ActivatorUtilities.CreateInstance<LightningPaymentMethodBitpayAPIExtension>(p, pmi));
    }
}
