# Pre-publication engineering review — 2026-10-02

Scope: XBTPay's changes against BTCPay Server v2.4.4 and NBXplorer v2.6.13,
the CLN gateway, rate conversion, payment toggles, and public build/deployment
materials. This was a source review and targeted verification, not an independent
audit or a review of every upstream dependency.

## Corrections

- Removed upstream's XBT-to-BTC network fallback. Missing XBT configuration now
  fails closed, rather than returning a SHA-256 BTC network. A regression test
  checks both `XBT` and `xbt` queries.
- Made CLN identity an explicit required configuration value. Empty/malformed
  node IDs fail startup and compatibility checks. Public files contain no local
  deployment defaults or administrator bootstrap scripts.
- Retained server-side rejection of private-key storage for XBT in the indexer,
  and readonly wallet restrictions in BTCPay. Public documentation clearly
  distinguishes receiving support from unified-signature spending support.
- Added BTCB2 as a 1:1 pricing alias for XBT, including dropdown and exact-rate
  checks. It cannot fall back to BTC pricing or create a second payment asset.

## Covered checks

- All four combinations of per-store on-chain/Lightning switches; unrelated
  payment method settings are retained. Settings require store-management
  authorization and antiforgery validation. Existing invoices remain monitored.
- Restricted CLN methods, own-invoice filtering, unpaid-only deletion, node and
  feature compatibility, private hints, and skipping unrelated payment events.
- Fixed satoshi conversion, BTCB2 conversion, currency dropdown, and rejection
  of stale, future-dated, nonpositive or wrong-pair exchange quotes.
- Knots header hash vectors before/at activation and recent heights; complete
  XBT block serialization and merkle-root round trip.
- Previously verified on a private test host: an operator-paid 10,000-sat
  Lightning checkout settled; live NBXplorer synchronized; address derivation
  matched Knots; a confirmed XBT transaction matched its block and confirmations;
  anonymous indexer access and private-key storage were rejected.

## Build and rollout verification

- Clean build: 10 BTCPay tests, 2 NBXplorer tests and 10 gateway tests passed;
  all three Docker images built successfully.
- Gitleaks 8.30.1 found no secrets in the public source snapshot.
- Updated Umbrel build: gateway identity and unpaid invoice create/read/cancel
  checks passed; Lightning and indexer monitoring reconnected; the previously
  paid checkout still reports Settled over trusted HTTPS. No payment was sent.

## LND backend verification — 2026-10-03

Run against a live XBT mainnet LND node (`0.21.3-beta-blake2b.14`) and Knots
`29.4.2` node, with the images built by `scripts/build.sh` (all focused tests
passed) and `COMPOSE_PROFILES=` (no CLN gateway):

- `scripts/lnd-connection.sh` accepted the node (bit 512 required, 515 present,
  synced) and baked a macaroon with `info:read`, `invoices:read` and
  `invoices:write` under its own root key id. With it, REST calls to send a
  payment (`/v2/router/send`), open a channel, send on-chain and read balances or
  channels returned `permission denied`; `getinfo` and invoices worked.
- BTCPay created XBT Lightning invoices through the node: the BOLT11 decoded to
  the node's key, carried feature 512, and appeared in the node as open invoices.
  No Lightning payment was received yet (the test channel had no inbound liquidity).
- On-chain: receiving addresses derived by BTCPay from a BIP84 account key matched
  `deriveaddresses` on the Knots node. A paid on-chain merchant checkout: a
  10,000-sat invoice paid from the node's wallet was detected in the mempool as
  Processing within seconds and became Settled after one confirmation (block 975272).

## Remaining limits

- No fresh paid on-chain merchant checkout or induced reorganization test yet.
- Large invoice-history load, hostile-user stress, dependency vulnerability audit,
  pruned historical recovery and every header-v2 flag combination are not covered.
- Public Docker configuration needs operator-specific RPC/socket permissions and
  HTTPS. It does not provision a full node, channels, funds or certificates.
- External wallet recovery/spending must support XBT; SHA-256 BTC signing is not
  a substitute. Paid refunds/payouts and BOLT12 merchant checkout are unsupported.

Do not interpret passing tests or public source availability as an audit or a
guarantee of fund safety. Report issues without posting secrets, seeds or RPC
credentials.
