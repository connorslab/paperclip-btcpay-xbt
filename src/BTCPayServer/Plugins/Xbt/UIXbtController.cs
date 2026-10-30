using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.Xbt;

public class XbtSettings
{
    public bool OnChainEnabled { get; set; }
    public bool LightningEnabled { get; set; }
    public bool Bolt12Enabled { get; set; }
    public static XbtSettings From(StoreBlob blob) => new() {
        OnChainEnabled = !blob.IsExcluded(PaymentTypes.CHAIN.GetPaymentMethodId("XBT")),
        LightningEnabled = !blob.IsExcluded(PaymentTypes.LN.GetPaymentMethodId("XBT")),
        Bolt12Enabled = !blob.IsExcluded(Bolt12.GetPaymentMethodId("XBT"))
    };
    public static XbtSettings From(StoreData store)
    {
        var settings = From(store.GetStoreBlob());
        // BOLT 12 is opt-in: a store has it only once the switch below was turned on.
        settings.Bolt12Enabled &= store.GetPaymentMethodConfig(Bolt12.GetPaymentMethodId("XBT")) is not null;
        return settings;
    }
    public void Apply(StoreBlob blob)
    {
        blob.SetExcluded(PaymentTypes.CHAIN.GetPaymentMethodId("XBT"), !OnChainEnabled);
        blob.SetExcluded(PaymentTypes.LN.GetPaymentMethodId("XBT"), !LightningEnabled);
        blob.SetExcluded(Bolt12.GetPaymentMethodId("XBT"), !Bolt12Enabled);
    }
    public void Apply(StoreData store)
    {
        var blob = store.GetStoreBlob();
        Apply(blob);
        store.SetStoreBlob(blob);
        var bolt12 = Bolt12.GetPaymentMethodId("XBT");
        if (Bolt12Enabled && store.GetPaymentMethodConfig(bolt12) is null)
            store.SetPaymentMethodConfig(bolt12, new Newtonsoft.Json.Linq.JObject());
    }
}

[Route("stores/{storeId}/xbt")]
[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
public class UIXbtController(StoreRepository repository) : Controller
{
    [HttpGet]
    public IActionResult Settings() => View("~/Plugins/Xbt/Views/Settings.cshtml", XbtSettings.From(HttpContext.GetStoreData()));

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Settings(XbtSettings model)
    {
        var store = HttpContext.GetStoreData();
        model.Apply(store);
        await repository.UpdateStore(store);
        TempData[WellKnownTempData.SuccessMessage] = "XBT payment settings saved. Changes apply to new invoices.";
        return RedirectToAction(nameof(Settings), new { storeId = store.Id });
    }
}
