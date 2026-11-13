# Stripe setup for Darvazeh

Business country: **New Zealand**. Legal entity: **Niliarya Limited**. Trading name: **ND ORBIT**.
Product brand and website: **Darvazeh**, https://darvazeh.daryajan.com/.

This integration sells **one period per payment**. It does not create recurring Stripe
subscriptions, save a card for automatic renewals, or charge again when a period ends.
The app's membership records are separate from Stripe's recurring-subscription product.

Checkout uses payment methods enabled in your Stripe Dashboard. The current Stripe
API no longer accepts an explicit `payment_method_types` list for Checkout creation.
Manage eligible methods under **Settings → Payment methods**.

## 1. Confirm eligibility and activate the business account

Stripe supports New Zealand businesses. Because Darvazeh connects to phones in Iran,
describe that business model accurately to Stripe and get eligibility confirmed before
enabling live payments. Stripe restricts certain Iran-related business; having a New
Zealand company is not by itself approval for every product or customer.

- [Stripe global availability](https://stripe.com/global)
- [Stripe restricted businesses](https://stripe.com/legal/restricted-businesses)
- [Account setup](https://docs.stripe.com/get-started/account/set-up)

Create a Stripe account at https://dashboard.stripe.com/register. Select New Zealand
and the correct registered business type. Enter Niliarya Limited as the legal entity
and ND ORBIT as its trading/public business name where requested. Complete Stripe's
business and representative verification with the actual company registration,
business address, ownership/representative information, and identity documents Stripe
requests. Add the company's bank account for payouts. Enable two-factor authentication.

Provide the real support contact details and Darvazeh website. Set a recognizable
statement descriptor, such as ND ORBIT or DARVAZEH, if approved by Stripe. Make the
service, contact details, terms/privacy and refund policy accurate and accessible to
customers. Stripe may request further business details; answer through its Dashboard.

You can start integration testing in a Stripe sandbox before live account activation.
Live activation and product eligibility are separate requirements.

## 2. Set plan prices in Darvazeh

Sign in as `admin@ndorbit.co.nz`, open **Administration → Membership plans**, and edit
Silver. Its existing duration/message allowance remains 30 days / 200 messages.
Its USD price is **$2.50** (250 cents), with 10 email forwards per period. Gold costs **$3.50** (350 cents), with 200 messages and 200 email forwards over 30 days. Toman prices remain unset.

- USD input is in dollars, for example `12.34` means **USD 12.34**. The server stores
  `1234` cents. This example is not a proposed Silver price.
- Toman input is a whole-number price, independently chosen. It is not an automatic
  currency conversion of the USD price.
- Leave a price empty to disable purchases in that currency.
- Enable **Available for customer purchase** when the prices are ready.
- The free three-day/20-message trial stays free and cannot be sold through checkout.

Prices shown in USD are the exact total submitted to card checkout. This release does
not automatically add tax, enable Stripe Tax, currency-localize the charge, add
discounts, or turn on recurring billing. Configure prices/accounting accordingly.
Stripe converts settlement currency when applicable; see its currency/payout guidance
for your bank account and fees: https://docs.stripe.com/currencies.

Existing orders keep their original amount, duration, allowance and included features.
Editing a plan affects new checkouts; it does not rewrite already-paid periods.

## 3. Connect a sandbox first

In the Stripe Dashboard, create/select a sandbox and obtain its **secret API key**.
Use a standard sandbox secret key initially; restricted keys must have the API
permissions needed to create/retrieve Checkout Sessions and retrieve payment/charge
records. Do not paste secret keys into chat, frontend code, Git, or a public document.

Add a webhook/event destination for your own account with this HTTPS endpoint:

`https://api1.daryajan.com/api/billing/stripe/webhook`

Subscribe to these snapshot event types:

- `checkout.session.completed`
- `checkout.session.async_payment_succeeded`
- `checkout.session.async_payment_failed`
- `checkout.session.expired`
- `charge.refunded`

Copy the destination's **signing secret** (`whsec_...`). This is different from the
API secret key. The signing secret from `stripe listen` is only for that CLI listener;
it is not the Dashboard destination's signing secret.

Put these values in this project's private root `.env`:

```dotenv
Billing__StripeEnabled=true
Billing__StripeSecretKey=sk_test_REPLACE_LOCALLY
Billing__StripeWebhookSecret=whsec_REPLACE_LOCALLY
Billing__PortalOrigin=https://darvazeh.daryajan.com
Billing__LegalName=Niliarya Limited
Billing__TradingName=ND ORBIT
```

No publishable key is required for this redirect-based Stripe-hosted checkout.
No manual Stripe Product/Price setup is required: checkout uses the plan's saved
USD price to create a one-time line item server-side.

Validate and deploy the settings from the repository root:

```powershell
python scripts/deployment-env.py check
./scripts/deploy.ps1 apply -Local -Tag YOUR_DEPLOYED_RELEASE_TAG
```

Full apply writes the private keys into the namespace-local Kubernetes Secret and
rolls application pods via a settings checksum. It does not bake keys into images.
The admin payments section shows whether card checkout is disconnected, in test mode,
or live. A visible test-mode notice is shown to customers in a sandbox.

## 4. Test the complete flow

Use a sandbox account, test key and the matching sandbox webhook destination.
Make a disposable test purchase from **My membership**, using Stripe's documented
test card data. Do not use a real card in sandbox mode.

Confirm the exact USD amount, plan duration, allowance and merchant details on
Stripe's hosted page. After payment, confirm the order becomes paid and the membership
starts. A trial is replaced immediately by the purchased period. An unexpired paid
period with remaining allowance is preserved; further purchases wait in order and
start on the next account use after expiry or exhaustion.

Check canceled checkout, a declined card, webhook retries and a sandbox refund.
The API verifies the raw webhook signature and retrieves payment details from Stripe;
opening a success URL alone cannot activate a plan. Repeated notifications activate
an order once. A full refund expires that order's active entitlement or removes the
queued entitlement; a partial refund records the amount without removing access.
An administrator's explicit sending/receiving restrictions survive purchases.

If a return page temporarily says it is confirming payment, it is waiting for verified
provider state. It polls the server and can recover from a missing webhook. Customer
and admin payment history persists in SQLite. No card number or CVC is stored here.

Local regression tests use a protocol fixture, not a real Stripe account:

```powershell
dotnet build server/FieldSense.Api
python scripts/verify-billing.py
python scripts/verify-membership.py
```

Provider sandbox testing is still required after keys are connected.

## 5. Switch to live only after activation and testing

Complete Stripe's business activation and eligibility confirmation. Create the same
webhook destination in **live mode**, copy its live signing secret, and replace the
test secret API key with the live key in root `.env`. Test and live secrets/destinations
are separate. Run validation and full apply again. Confirm the Dashboard can deliver
events to the live endpoint. Turn on Stripe's payment receipts if desired.

References: [Checkout fulfillment](https://docs.stripe.com/checkout/fulfillment?ui=full-page),
[webhook setup](https://docs.stripe.com/events/set-up-events),
[webhook signature troubleshooting](https://docs.stripe.com/events/manage-webhook-endpoints#signature-errors).

## Toman and the future Ezpay merchant connection

The app stores/displays a separate whole-toman price and reserves the wallet option in
the membership UI. The toman payment button remains disabled until the Ezpay merchant
method is implemented and configured. It does not call Stripe for toman and does not
pretend a wallet payment succeeded. If the later merchant API expects conventional
rial amounts, its adapter must explicitly convert `toman × 10`; if it expects toman,
it must submit the unchanged toman value. This must follow that merchant API's actual
currency contract. The conversion helper uses checked integer arithmetic.
