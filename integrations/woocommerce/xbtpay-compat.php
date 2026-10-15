<?php
/**
 * Plugin Name: XBTPay WooCommerce compatibility
 * Description: Preserve canonical XBT payment IDs when using the BTCPay default gateway.
 */
add_filter('woocommerce_payment_gateways', function ($gateways) {
    if (!class_exists('BTCPayServer\\WC\\Gateway\\DefaultGateway')) return $gateways;
    if (!class_exists('XBTPay_WooCommerce_Gateway')) {
        class XBTPay_WooCommerce_Gateway extends \BTCPayServer\WC\Gateway\DefaultGateway {
            public function getPaymentMethods(): array {
                return array_map(static function ($method) {
                    return ['XBT_LN' => 'XBT-LN', 'XBT_CHAIN' => 'XBT-CHAIN'][$method] ?? $method;
                }, parent::getPaymentMethods());
            }
        }
    }
    foreach ($gateways as &$gateway) {
        if ($gateway === 'BTCPayServer\\WC\\Gateway\\DefaultGateway') $gateway = 'XBTPay_WooCommerce_Gateway';
    }
    return $gateways;
}, 100);
