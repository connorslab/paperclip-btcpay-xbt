# WooCommerce checkout

Use the official BTCPay Greenfield WooCommerce plugin. XBT invoice payment methods
are `XBT-LN` and `XBT-CHAIN`. The plugin may submit underscore aliases;
XBTPay normalizes those through its legacy payment-method parser.

For older deployed XBTPay builds, copy `xbtpay-compat.php` to WordPress's
`wp-content/mu-plugins/`. It maps only the two XBT aliases for the default gateway,
leaving other networks unchanged. It does not enable any gateway or payment method.

Give WooCommerce store-scoped invoice-create, invoice-view and store-settings-view
permissions. Register a signed webhook separately. Do not grant spending access.
Keep credentials outside source control. Verify settlement against the invoice API
before fulfillment; never fulfill based solely on the browser return.

Before launch, test invoice totals including shipping/tax, signature rejection,
duplicate events, expiration, partial payment, and an actual settled payment.
Private Docker hostnames used in staging are not customer checkout URLs; configure
and test the final HTTPS invoice and return URLs before enabling checkout.

Runtime images normalize permissions on public `wwwroot` assets so deployments
using an unprivileged UID can render checkout. Wallet and database permissions
are unaffected.
