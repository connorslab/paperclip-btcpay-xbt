#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Lightning;
using BTCPayServer.Lightning.LND;
using BTCPayServer.Logging;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBitcoin.DataEncoders;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Xbt;

/// <summary>
/// BOLT 12 checkout for stores whose Lightning node is an LND with the offers RPC
/// (paulscode/lightning-fork). Each invoice gets its own single-use offer for the exact
/// amount, expiring with the invoice; <see cref="Bolt12Listener"/> records the payment
/// once the node reports one of the offer's invoices as settled.
/// </summary>
public static class Bolt12
{
    public static readonly PaymentType PaymentType = new("BOLT12");
    public static PaymentMethodId GetPaymentMethodId(string cryptoCode) => PaymentType.GetPaymentMethodId(cryptoCode);
}

public class Bolt12PaymentMethodConfig
{
}

public class Bolt12PromptDetails
{
    /// <summary>The offer id in hex, as LND and Core Lightning compute it.</summary>
    public string OfferId { get; set; } = "";
}

/// <summary>
/// What is kept of a BOLT 12 payment. The field names match <see cref="LightningPaymentData"/>, so
/// the invoice page lists it with the other Lightning payments: the paid invoice (lni1…) as its
/// destination and the preimage as its proof.
/// </summary>
public class Bolt12PaymentDetails
{
    public string PaymentHash { get; set; } = "";
    public string? Preimage { get; set; }
    [JsonProperty("BOLT11")]
    public string? Invoice { get; set; }
    /// <summary>The payer's note from the invoice request (invreq_payer_note), if any.</summary>
    public string? PayerNote { get; set; }
}

public class Bolt12PaymentHandler : IPaymentMethodHandler, ILightningPaymentHandler
{
    private readonly BTCPayNetwork _network;
    private readonly IServiceProvider _serviceProvider;
    public JsonSerializer Serializer { get; }
    public PaymentMethodId PaymentMethodId { get; }
    public BTCPayNetwork Network => _network;

    public Bolt12PaymentHandler(PaymentMethodId paymentMethodId, BTCPayNetwork network, IServiceProvider serviceProvider)
    {
        PaymentMethodId = paymentMethodId;
        _network = network;
        _serviceProvider = serviceProvider;
        Serializer = BlobSerializer.CreateSerializer(network.NBitcoinNetwork).Serializer;
    }

    public Task BeforeFetchingRates(PaymentMethodContext context)
    {
        context.Prompt.Currency = _network.CryptoCode;
        context.Prompt.PaymentMethodFee = 0m;
        context.Prompt.Divisibility = 11;
        context.Prompt.RateDivisibility = 8;
        return Task.CompletedTask;
    }

    public async Task ConfigurePrompt(PaymentMethodContext context)
    {
        var invoice = context.InvoiceEntity;
        if (invoice.Type == InvoiceType.TopUp)
            throw new PaymentMethodUnavailableException("BOLT 12 checkout needs an invoice with an amount.");
        var offers = CreateOffersClient(context.Store)
            ?? throw new PaymentMethodUnavailableException("BOLT 12 needs the store's Lightning node to be an LND with BOLT 12 offers.");
        var due = context.Prompt.Calculate().Due;
        var amountMsat = (ulong)Math.Ceiling(due * 100_000_000_000m);
        if (amountMsat == 0)
            throw new PaymentMethodUnavailableException("Nothing is due.");
        var expiry = invoice.ExpirationTime.ToUnixTimeSeconds();
        if (expiry <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            throw new PaymentMethodUnavailableException("The invoice has expired.");

        Bolt12Offer offer;
        using (var cts = new CancellationTokenSource(LightningLikePaymentHandler.LightningTimeout))
        {
            try
            {
                offer = await offers.CreateOffer(new Bolt12OfferRequest(
                    Description: GetDescription(context),
                    AmountMsat: amountMsat,
                    AbsoluteExpiry: (ulong)expiry,
                    Issuer: context.Store.StoreName,
                    Label: $"xbtpay:{invoice.Id}"), cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                throw new PaymentMethodUnavailableException("The lightning node did not reply in a timely manner");
            }
            catch (Exception ex) when (ex is not PaymentMethodUnavailableException)
            {
                throw new PaymentMethodUnavailableException($"Impossible to create a BOLT 12 offer ({ex.Message})", ex);
            }
        }
        // The node mints the same offer again for the same fields. The invoice id in the
        // description keeps offers apart, so an old offer here means it is already in use.
        if (!offer.Created)
            throw new PaymentMethodUnavailableException("The node returned an existing BOLT 12 offer.");
        context.Prompt.Destination = offer.Bolt12;
        context.Prompt.Details = JObject.FromObject(new Bolt12PromptDetails { OfferId = offer.OfferId }, Serializer);
        context.TrackedDestinations.Add(offer.OfferId);
    }

    static string GetDescription(PaymentMethodContext context)
    {
        var invoice = context.InvoiceEntity;
        var template = new TextTemplate(context.StoreBlob.LightningDescriptionTemplate)
        {
            NotFoundReplacement = path => $"{{{path}}}",
            ParsingErrorReplacement = path => $"{{{path}}}"
        };
        var description = template.Render(new JObject
        {
            ["StoreName"] = context.Store.StoreName,
            ["ItemDescription"] = invoice.Metadata.ItemDesc,
            ["OrderId"] = invoice.Metadata.OrderId
        });
        return $"{description} (invoice {invoice.Id})";
    }

    internal LndOffersClient? CreateOffersClient(Data.StoreData store)
    {
        var handlers = _serviceProvider.GetRequiredService<PaymentMethodHandlerDictionary>();
        var lnConfig = handlers.GetLightningConfig(store, _network);
        if (lnConfig is null)
            return null;
        var client = handlers.GetLightningHandler(_network).CreateLightningClient(lnConfig);
        return LndOffersClient.TryCreate(client);
    }

    public Bolt12PaymentMethodConfig ParsePaymentMethodConfig(JToken config)
        => config.ToObject<Bolt12PaymentMethodConfig>(Serializer) ?? new Bolt12PaymentMethodConfig();
    object IPaymentMethodHandler.ParsePaymentMethodConfig(JToken config) => ParsePaymentMethodConfig(config);

    public Bolt12PromptDetails ParsePaymentPromptDetails(JToken details)
        => details.ToObject<Bolt12PromptDetails>(Serializer) ?? throw new FormatException($"Invalid {nameof(Bolt12PromptDetails)}");
    object IPaymentMethodHandler.ParsePaymentPromptDetails(JToken details) => ParsePaymentPromptDetails(details);

    public LightningPaymentData ParsePaymentDetails(JToken details)
        => details.ToObject<LightningPaymentData>(Serializer) ?? throw new FormatException($"Invalid {nameof(LightningPaymentData)}");
    object IPaymentMethodHandler.ParsePaymentDetails(JToken details) => ParsePaymentDetails(details);
}

public record Bolt12OfferRequest(string Description, ulong AmountMsat, ulong AbsoluteExpiry, string? Issuer, string Label);
public record Bolt12Offer(string OfferId, string Bolt12, bool Created);
public record Bolt12OfferInvoice(string PaymentHash, string State, ulong AmountPaidMsat, DateTimeOffset CreatedAt, string? Bolt12 = null);

/// <summary>
/// The offers REST API of paulscode/lightning-fork (/v2/offers), reached through the
/// HttpClient and macaroon BTCPay already set up for the store's LND. Needs the same
/// invoices:read and invoices:write permissions as BOLT 11 checkout.
/// </summary>
public class LndOffersClient
{
    static readonly FieldInfo? HttpClientField = typeof(LndSwaggerClient).GetField("_httpClient", BindingFlags.Instance | BindingFlags.NonPublic);
    static readonly FieldInfo? AuthenticationField = typeof(LndSwaggerClient).GetField("_Authentication", BindingFlags.Instance | BindingFlags.NonPublic);

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly LndAuthentication _authentication;

    public LndOffersClient(HttpClient http, string baseUrl, LndAuthentication authentication)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
        _authentication = authentication;
    }

    /// <summary>Whether this build of BTCPayServer.Lightning still exposes what TryCreate reads.</summary>
    public static bool IsSupported => HttpClientField is not null && AuthenticationField is not null;

    public static LndOffersClient? TryCreate(ILightningClient client)
    {
        if (client is not LndClient lnd || !IsSupported)
            return null;
        var swagger = lnd.SwaggerClient;
        if (HttpClientField!.GetValue(swagger) is not HttpClient http ||
            AuthenticationField!.GetValue(swagger) is not LndAuthentication authentication)
            return null;
        return new LndOffersClient(http, swagger.BaseUrl, authentication);
    }

    public async Task<Bolt12Offer> CreateOffer(Bolt12OfferRequest request, CancellationToken cancellationToken)
    {
        var body = new JObject
        {
            ["description"] = request.Description,
            ["amount_msat"] = request.AmountMsat.ToString(CultureInfo.InvariantCulture),
            ["absolute_expiry"] = request.AbsoluteExpiry.ToString(CultureInfo.InvariantCulture),
            ["label"] = request.Label
        };
        if (!string.IsNullOrEmpty(request.Issuer))
            body["issuer"] = request.Issuer;
        var response = await Post("/v2/offers", body, cancellationToken);
        var offer = response["offer"] as JObject ?? throw new FormatException("No offer in the node's reply");
        return new Bolt12Offer(
            ToHex(offer.Value<string>("offer_id")),
            offer.Value<string>("bolt12") ?? throw new FormatException("No bolt12 in the node's reply"),
            response.Value<bool?>("created") ?? false);
    }

    public async Task<Bolt12OfferInvoice[]> ListOfferInvoices(string offerIdHex, CancellationToken cancellationToken)
    {
        var response = await Post("/v2/offers/invoices", new JObject { ["offer_id"] = ToBase64(offerIdHex) }, cancellationToken);
        if (response["invoices"] is not JArray invoices)
            return Array.Empty<Bolt12OfferInvoice>();
        return invoices.OfType<JObject>()
            // The node filters by offer id; check again so a node that ignores the filter
            // can never credit another offer's payment to this invoice.
            .Where(i => ToHex(i.Value<string>("offer_id")) == offerIdHex)
            .Select(i => new Bolt12OfferInvoice(
                ToHex(i.Value<string>("payment_hash")),
                i.Value<string>("state") ?? "",
                ParseULong(i.Value<string>("amount_paid_msat")),
                DateTimeOffset.FromUnixTimeSeconds((long)ParseULong(i.Value<string>("created_at"))),
                i.Value<string>("bolt12")))
            .ToArray();
    }

    public Task DisableOffer(string offerIdHex, CancellationToken cancellationToken)
        => Post("/v2/offers/disable", new JObject { ["offer_id"] = ToBase64(offerIdHex) }, cancellationToken);

    /// <summary>The preimage of a settled invoice (lnrpc LookupInvoice), in hex, if the node gives it.</summary>
    public async Task<string?> GetPreimage(string paymentHashHex, CancellationToken cancellationToken)
    {
        var response = await Send(HttpMethod.Get, $"/v1/invoice/{paymentHashHex}", null, cancellationToken);
        var preimage = response.Value<string>("r_preimage");
        return string.IsNullOrEmpty(preimage) ? null : ToHex(preimage);
    }

    /// <summary>The payer's note of an offer's invoice (lni1…), if the request had one.</summary>
    public async Task<string?> GetPayerNote(string bolt12Invoice, CancellationToken cancellationToken)
    {
        var response = await Post("/v2/offers/decode", new JObject { ["bolt12"] = bolt12Invoice }, cancellationToken);
        var note = (response["invoice_request"] as JObject)?.Value<string>("payer_note");
        return string.IsNullOrWhiteSpace(note) ? null : note;
    }

    Task<JObject> Post(string path, JObject body, CancellationToken cancellationToken)
        => Send(HttpMethod.Post, path, body, cancellationToken);

    async Task<JObject> Send(HttpMethod method, string path, JObject? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, _baseUrl + path);
        if (body is not null)
            request.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
        _authentication.AddAuthentication(request);
        using var response = await _http.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            string message = content;
            try { message = JObject.Parse(content).Value<string>("message") ?? content; }
            catch (JsonException) { }
            throw new HttpRequestException($"{(int)response.StatusCode}: {message}");
        }
        return JObject.Parse(content);
    }

    static ulong ParseULong(string? value) => ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : 0;
    static string ToHex(string? base64) => Encoders.Hex.EncodeData(Convert.FromBase64String(base64 ?? ""));
    static string ToBase64(string hex) => Convert.ToBase64String(Encoders.Hex.DecodeData(hex));
}

public class Bolt12CheckoutModelExtension : ICheckoutModelExtension
{
    private readonly BTCPayNetwork _network;
    public Bolt12CheckoutModelExtension(PaymentMethodId paymentMethodId, BTCPayNetwork network)
    {
        PaymentMethodId = paymentMethodId;
        _network = network;
    }
    public PaymentMethodId PaymentMethodId { get; }
    public string Image => _network.LightningImagePath;
    public string Badge => "⚡";

    public void ModifyCheckoutModel(CheckoutModelContext context)
    {
        if (context is not { Handler: Bolt12PaymentHandler })
            return;
        context.Model.CheckoutBodyComponentName = LNCheckoutModelExtension.CheckoutBodyComponentName;
        if (string.IsNullOrEmpty(context.Prompt.Destination))
            return;
        context.Model.Address = context.Prompt.Destination;
        context.Model.InvoiceBitcoinUrl = Bolt12PaymentLinkExtension.GetLink(context.Prompt.Destination);
        // Offers have no checksum, so upper case is fine and makes a denser QR code.
        context.Model.InvoiceBitcoinUrlQR = $"lightning:{context.Prompt.Destination.ToUpperInvariant()}";
    }
}

public class Bolt12PaymentLinkExtension : IPaymentLinkExtension
{
    public Bolt12PaymentLinkExtension(PaymentMethodId paymentMethodId) => PaymentMethodId = paymentMethodId;
    public PaymentMethodId PaymentMethodId { get; }
    public static string GetLink(string offer) => $"lightning:{offer.ToLowerInvariant()}";
    public string? GetPaymentLink(PaymentPrompt prompt, IUrlHelper? urlHelper)
        => string.IsNullOrEmpty(prompt.Destination) ? null : GetLink(prompt.Destination);
}

public class Bolt12PaymentMethodBitpayAPIExtension : IPaymentMethodBitpayAPIExtension
{
    public Bolt12PaymentMethodBitpayAPIExtension(PaymentMethodId paymentMethodId) => PaymentMethodId = paymentMethodId;
    public PaymentMethodId PaymentMethodId { get; }
    public void PopulateCryptoInfo(InvoiceCryptoInfo cryptoInfo, Models.InvoiceResponse dto, PaymentPrompt prompt, IUrlHelper urlHelper)
    {
    }
}

/// <summary>
/// Polls the store's node for the offers of pending invoices, records settled payments,
/// and disables an offer once its invoice is no longer pending.
/// </summary>
public class Bolt12Listener : BackgroundService
{
    static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private readonly PaymentMethodId _paymentMethodId;
    private readonly BTCPayNetwork _network;
    private readonly IServiceProvider _serviceProvider;
    private readonly InvoiceRepository _invoiceRepository;
    private readonly StoreRepository _storeRepository;
    private readonly PaymentService _paymentService;
    private readonly EventAggregator _eventAggregator;
    private readonly Logs _logs;
    // A payment can still settle shortly after its invoice stopped being pending: the
    // payer may have fetched the offer's invoice just before the deadline.
    static readonly TimeSpan LatePaymentWindow = TimeSpan.FromHours(1);
    record OpenOffer(string StoreId, string InvoiceId)
    {
        public DateTimeOffset? LeftPendingAt { get; set; }
        public bool Disabled { get; set; }
    }
    // Offers seen on pending invoices, by offer id.
    private readonly ConcurrentDictionary<string, OpenOffer> _openOffers = new();

    public Bolt12Listener(PaymentMethodId paymentMethodId, BTCPayNetwork network, IServiceProvider serviceProvider,
        InvoiceRepository invoiceRepository, StoreRepository storeRepository, PaymentService paymentService,
        EventAggregator eventAggregator, Logs logs)
    {
        _paymentMethodId = paymentMethodId;
        _network = network;
        _serviceProvider = serviceProvider;
        _invoiceRepository = invoiceRepository;
        _storeRepository = storeRepository;
        _paymentService = paymentService;
        _eventAggregator = eventAggregator;
        _logs = logs;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Poll(stoppingToken);
            }
            catch when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logs.PayServer.LogWarning(ex, "{CryptoCode} (BOLT 12): error while checking offers", _network.CryptoCode);
            }
            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    async Task Poll(CancellationToken cancellationToken)
    {
        var handlers = _serviceProvider.GetRequiredService<PaymentMethodHandlerDictionary>();
        if (handlers.TryGet(_paymentMethodId) is not Bolt12PaymentHandler handler)
            return;
        var invoices = await _invoiceRepository.GetMonitoredInvoices(_paymentMethodId, cancellationToken);
        var pending = new HashSet<string>();
        var clients = new Dictionary<string, LndOffersClient?>();
        foreach (var invoice in invoices)
        {
            var prompt = invoice.GetPaymentPrompt(_paymentMethodId);
            if (prompt is not { Activated: true, Details: not null })
                continue;
            var offerId = handler.ParsePaymentPromptDetails(prompt.Details).OfferId;
            if (string.IsNullOrEmpty(offerId))
                continue;
            pending.Add(offerId);
            _openOffers.TryAdd(offerId, new OpenOffer(invoice.StoreId, invoice.Id));
            await CheckOffer(handler, clients, offerId, invoice.StoreId, invoice.Id, cancellationToken);
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var (offerId, open) in _openOffers.ToArray())
        {
            if (pending.Contains(offerId))
                continue;
            open.LeftPendingAt ??= now;
            var offers = await GetClient(handler, clients, open.StoreId);
            if (!open.Disabled)
            {
                try
                {
                    if (offers is not null)
                        await offers.DisableOffer(offerId, cancellationToken);
                    open.Disabled = true;
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    // The offer expires with its invoice anyway; try again on the next round.
                    _logs.PayServer.LogInformation("{CryptoCode} (BOLT 12): cannot disable offer {OfferId}: {Message}",
                        _network.CryptoCode, offerId, ex.Message);
                }
            }
            await CheckOffer(handler, clients, offerId, open.StoreId, open.InvoiceId, cancellationToken);
            if (open.Disabled && now - open.LeftPendingAt > LatePaymentWindow)
                _openOffers.TryRemove(offerId, out _);
        }
    }

    async Task CheckOffer(Bolt12PaymentHandler handler, Dictionary<string, LndOffersClient?> clients,
        string offerId, string storeId, string invoiceId, CancellationToken cancellationToken)
    {
        var offers = await GetClient(handler, clients, storeId);
        if (offers is null)
            return;
        try
        {
            foreach (var paid in await offers.ListOfferInvoices(offerId, cancellationToken))
            {
                if (paid is { State: "SETTLED", AmountPaidMsat: > 0 })
                    await RecordPayment(handler, offers, invoiceId, paid, cancellationToken);
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logs.PayServer.LogWarning("{CryptoCode} (BOLT 12): cannot check offer {OfferId} of invoice {InvoiceId}: {Message}",
                _network.CryptoCode, offerId, invoiceId, ex.Message);
        }
    }

    async Task<LndOffersClient?> GetClient(Bolt12PaymentHandler handler, Dictionary<string, LndOffersClient?> clients, string storeId)
    {
        if (clients.TryGetValue(storeId, out var client))
            return client;
        var store = await _storeRepository.FindStore(storeId);
        client = store is null ? null : handler.CreateOffersClient(store);
        clients[storeId] = client;
        return client;
    }

    async Task RecordPayment(Bolt12PaymentHandler handler, LndOffersClient offers, string invoiceId, Bolt12OfferInvoice paid,
        CancellationToken cancellationToken)
    {
        var invoiceEntity = await _invoiceRepository.GetInvoice(invoiceId);
        if (invoiceEntity is null)
            return;
        // Already recorded: nothing more to ask the node.
        if (invoiceEntity.GetPayments(false).Any(p => p.PaymentMethodId == _paymentMethodId && p.Id == paid.PaymentHash))
            return;
        // The proof of payment and the payer's note are a nicety: a node that can't give them
        // must not keep the payment from being recorded.
        string? preimage = null, payerNote = null;
        try
        {
            preimage = await offers.GetPreimage(paid.PaymentHash, cancellationToken);
            if (!string.IsNullOrEmpty(preimage) &&
                Encoders.Hex.EncodeData(NBitcoin.Crypto.Hashes.SHA256(Encoders.Hex.DecodeData(preimage))) != paid.PaymentHash)
                preimage = null;
            if (!string.IsNullOrEmpty(paid.Bolt12))
                payerNote = await offers.GetPayerNote(paid.Bolt12, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logs.PayServer.LogInformation("{CryptoCode} (BOLT 12): no proof or note for invoice {InvoiceId}: {Message}",
                _network.CryptoCode, invoiceId, ex.Message);
        }
        // Before the payment, so the "payment received" event (emails, webhooks) can show it.
        if (payerNote is not null)
            await _invoiceRepository.UpdateInvoiceMetadata(invoiceId, "payerNote", payerNote);
        invoiceEntity = await _invoiceRepository.GetInvoice(invoiceId);
        if (invoiceEntity is null)
            return;
        var paymentData = new PaymentData()
        {
            Id = paid.PaymentHash,
            Created = paid.CreatedAt,
            Status = PaymentStatus.Settled,
            Currency = _network.CryptoCode,
            InvoiceDataId = invoiceId,
            Amount = LightMoney.MilliSatoshis((long)paid.AmountPaidMsat).ToDecimal(LightMoneyUnit.BTC),
        }.Set(invoiceEntity, handler, new Bolt12PaymentDetails
        {
            PaymentHash = paid.PaymentHash, Preimage = preimage, Invoice = paid.Bolt12, PayerNote = payerNote
        });

        var payment = await _paymentService.AddPayment(paymentData, [paid.PaymentHash]);
        if (payment is null)
            return; // already recorded
        _logs.PayServer.LogInformation("{CryptoCode} (BOLT 12): invoice {InvoiceId} received {Amount} msat",
            _network.CryptoCode, invoiceId, paid.AmountPaidMsat);
        var invoice = await _invoiceRepository.GetInvoice(invoiceId);
        if (invoice is not null)
            _eventAggregator.Publish(new InvoiceEvent(invoice, InvoiceEvent.ReceivedPayment) { Payment = payment });
    }
}
