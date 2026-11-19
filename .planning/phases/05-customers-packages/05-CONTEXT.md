# Phase 5: Customers & Packages — Context

**Source:** The approved project plan and the requirements Q&A, written 2026-11-20. The user asked for autonomous execution, so Claude made the decisions marked (default).

## Phase Boundary

- Staff can create, edit and search customers.
- Members get member pricing (PRIC-03).
- Staff define prepaid time packages and sell them to customers. A sale adds minutes to the customer's balance and records the payment.
- Sessions can be linked to a customer and can draw time from the customer's balance.
- General POS orders and receipts are Phase 6. This phase adds the minimal `Payment` record that Phase 6 extends.

## Existing Code to Build On
- Phase 2: `PricingEngine` (a pure function of intervals, rules and time zone), `SessionService`, the `Session`/`SessionSegment`/`SessionEvent` entities, `RateRule` and the dashboard card start dialogs.
- Phase 3/4: the agent and TV hooks fire on session events. Nothing changes there, but don't break them.
- Phase 1 patterns: `ServiceResult` + `FailureKind`, `IDbContextFactory`, the CRUD page style and `CafeWebApplicationFactory`.
- Read the 02-, 03- and 04-*-SUMMARY.md files for exact names.

## Locked Decisions

### Data model (one EF migration, via `dotnet tool run dotnet-ef`)
- `Customer`: Id, Name (required), Phone (string?, stored normalized to digits only and unique when not null), IsMember (bool), Notes?, IsActive (default true; soft-delete instead of hard delete) and CreatedAtUtc.
- `TimePackage`: Id, Name (unique), Minutes (1–10000), Price (decimal ≥ 0), StationTypeId? (null = usable on any station type) and IsActive.
- `CustomerMinuteLedger` holds the balance as a ledger, not a mutable column:
  - Fields: Id, CustomerId, StationTypeId? (the bucket; null = any type), MinutesDelta (+ credit / − debit), Reason (`LedgerReason { PackagePurchase, SessionDebit, SessionRefund, ManualAdjustment }`), SessionId?, PackageSaleId?, AtUtc and Note?.
  - Balance per bucket = SUM(MinutesDelta).
- `PackageSale`: Id, CustomerId, TimePackageId, Price (snapshot), Minutes (snapshot), StationTypeId (snapshot) and AtUtc.
- `Payment` is minimal now and extended in Phase 6:
  - Fields: Id, Amount (decimal), Method (`PaymentMethod { Cash, Card }`), AtUtc, PackageSaleId?, SessionId? and Note?.
  - Phase 6 will add OrderId.
- `Session.CustomerId?` (FK).
- `StationType.MemberHourlyRate?` (decimal; null means members pay the normal rate).
- `RateRule.AppliesTo` (`RateRuleAudience { Everyone, MembersOnly, GuestsOnly }`, default Everyone). Edit it on /rate-rules.

### Member pricing (PRIC-03)
- PricingEngine takes `isMember`.
  - The base hourly rate is `MemberHourlyRate ?? HourlyRate` for members and `HourlyRate` for guests.
  - Rules filter by AppliesTo, then select by Priority as before. A Multiplier rule multiplies the audience-appropriate base.
- A session is priced as a member session if its linked customer IsMember at start (snapshot it as `Session.IsMemberPricing`).
- Engine tests are mandatory: member vs guest base rate, a MembersOnly happy hour, a GuestsOnly rule ignored for members, and a member with no MemberHourlyRate falling back to the normal rate.

### Packages and balance (CUST-03, CUST-04, CUST-05)
- `/packages`: CRUD for TimePackage.
- **Selling** from the customer detail page: pick a package and payment method (Cash/Card). This creates the PackageSale, a Payment and a ledger credit in one transaction (CUST-04).
- **Starting from balance** (a third option in the start dialog when a customer with a balance is selected):
  - Choose minutes up to the usable balance for that station's type, which is the type-specific bucket plus the any-type bucket. The type-specific bucket is debited first.
  - Debit the ledger upfront. The session is prepaid with ChargedAmount 0 and new `SessionMode.Balance`, and the expiry flow works the same.
  - Extending a balance session debits more balance, or falls back to a paid prepaid extension if the balance is insufficient (staff choose).
  - Stopping early (default) refunds the unused whole minutes to the same buckets they came from, recorded as SessionRefund.
  - Expiry uses everything, so there's no refund.
- **Manual adjustment** on the customer page (+/− minutes with a required note), recorded as ManualAdjustment.

### Customers (CUST-01, CUST-02, CUST-06)
- `/customers` lists customers. Search box: case-insensitive contains on name, or digits-contains on phone; return at most 50 rows. Buttons are Add customer and Edit.
- `/customers/{id}` shows details: balance per bucket, package purchases, ledger history and the 20 most recent sessions.
- In the dashboard start dialogs, add an optional "Customer" search-select (type a name or phone, pick one, or quick-add with name + phone). The selected customer is linked to the session, member pricing shows as a "Member" tag on the card, and the customer name appears on the card.
- `CustomerService`, `PackageService` and `BalanceService` follow the ServiceResult pattern. Phone uniqueness conflicts surface on the field.

### Testing
- Service tests:
  - Customer CRUD, phone normalization and uniqueness, and search.
  - Package sale writes 3 rows atomically (sale, payment, ledger) and rolls back on failure.
  - Balance math with buckets.
  - A balance session's debit at start, refund on early stop, no refund on expiry, and extend from balance.
  - Member-priced session charge.
- Page smoke tests: /customers, /customers/{id} and /packages require auth and render.

## Claude's Discretion
- UI layout, and whether the customer picker is a modal or inline.
- Phone display formatting.

## Deferred
- Customer self-login at PCs, reservations → v2.
- Receipts for package sales → Phase 6 receipt printing can reuse Payment.

## Environment Notes
- Same as earlier phases: `export PATH="/usr/local/share/dotnet:$PATH"`, net10.0, Microsoft packages only, EF migrations only via `dotnet tool run dotnet-ef`, commits on main as dylenq with no attribution, and leave the untracked runtime files alone.
