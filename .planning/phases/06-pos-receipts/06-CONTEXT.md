# Phase 6: POS & Receipts — Context

**Source:** The approved project plan and the requirements Q&A, written 2026-11-20. The user asked for autonomous execution, so Claude made the decisions marked (default).

## Phase Boundary

- Staff manage products and ring up food/drink orders, attached to an active session (a tab), a customer, or a walk-in.
- Staff record payments (cash/card, record-only) for session charges, orders and packages, including settling everything owed when a session stops.
- Staff print receipts.

There is no payment-provider integration and no inventory (out of scope).

## Existing Code to Build On
- Phase 5 added a minimal `Payment` (Amount, Method Cash/Card, AtUtc, PackageSaleId?, SessionId?, Note?) and `Customer`. Extend `Payment`; do not duplicate it.
- The Phase 2 session stop flow, which shows the final charge in a confirmation, and `Session.ChargedAmount`.
- The dashboard card actions and the established page/service patterns.
- Read the 05-*-SUMMARY.md files and earlier summaries for exact names.

## Locked Decisions

### Data model (one EF migration, via `dotnet tool run dotnet-ef`)
- `Product`: Id, Name (unique, NOCASE), Category (string, required; e.g. Drinks, Snacks, Food), Price (decimal ≥ 0), IsActive and SortOrder.
- `Order`: Id, CreatedAtUtc, CustomerId?, SessionId?, Status (`OrderStatus { Open, Paid, Voided }`), Total (decimal) and Note?.
  - Walk-in = both CustomerId and SessionId null.
- `OrderLine`: Id, OrderId, ProductId, ProductNameSnapshot, UnitPriceSnapshot, Quantity (≥ 1) and LineTotal.
- `Payment` gains OrderId?. One payment may cover several items, so add `PaymentAllocation` (PaymentId, TargetType {Session, Order, PackageSale}, TargetId, Amount). A settle-up payment can then cover a session charge plus its tab orders. Package sales from Phase 5 create a single allocation; migrate existing Payment rows into allocations in the migration, or in a startup data fix if EF can't express it.
- Outstanding amount for a target = its charge/total − SUM(allocations to it). Voided orders owe nothing.

### POS UI (POS-01, POS-02)
- `/products`: CRUD with category, price, active and sort order.
- `/pos`: a touch-friendly page that works on a tablet.
  - A product grid grouped by category tabs; tapping adds to the cart and taps accumulate quantity.
  - The cart has quantity +/−, remove and a total.
  - An "Attach to" selector: Walk-in, Active session (lists stations with active sessions and the customer name if any), or Customer (search).
  - Actions:
    - **Charge now**: open the payment dialog and mark the order Paid when it's fully allocated.
    - **Add to tab**: only when attached to a session or customer; the order stays Open.
    - **Void**: for an open order, with confirmation.
  - An "Open tabs" panel lists open orders and unpaid session charges, each with a Settle button.

### Payments (POS-03)
- **Payment dialog** (a reusable component):
  - It shows the items being settled and their outstanding amounts, plus the total due.
  - Staff add one or more payments (Cash or Card + amount); a split such as cash + card is allowed. For cash, a "tendered" field shows the change due (change isn't stored).
  - Confirming writes the Payment(s) and Allocations in one transaction, then marks the items Paid or settled.
- **Session stop:** the stop confirmation now shows the session charge plus the session's open tab orders, with two buttons:
  - **Stop & settle**: opens the payment dialog for everything.
  - **Stop, pay later**: leaves the items outstanding and visible under Open tabs.
- **Package sale** from Phase 5 now uses the same payment dialog component.

### Receipts (POS-04)
- `Receipts/ReceiptBuilder` is pure. It turns a payment (or a set of allocations) into a `ReceiptDocument` containing:
  - Header lines from settings and the date/time in the cafe time zone.
  - Lines: session (station, duration, mode), order lines (qty × name @ price) and package sale.
  - Subtotal, payments by method and change.
  - A receipt number = Payment Id zero-padded.
- **Printers.** `IReceiptPrinter` has three implementations; the mode is selected in settings (`Receipts:Mode` = None | Network | WindowsPrinter | Browser, default Browser):
  - **`EscPosEncoder`:** our own minimal ESC/POS byte encoder. **No third-party package** (the Microsoft-only constraint overrides the plan's mention of ESCPOS_NET). It supports init (ESC @), align (ESC a n), bold (ESC E n), double height (GS ! n), text in code page 437/ASCII (anything else replaced with '?'), line feeds, partial cut (GS V 66 0) and a 42-column (80mm) or 32-column (58mm) layout from `Receipts:Columns`.
  - **`NetworkEscPosPrinter`:** a raw TCP connection to `Receipts:Host`:`Receipts:Port` (default 9100) with a 5s timeout.
  - **`WindowsRawPrinter`:** sends the ESC/POS bytes to a named Windows printer via winspool P/Invoke (OpenPrinter, StartDocPrinter RAW, WritePrinter). Guard it with `OperatingSystem.IsWindows()`; elsewhere it returns a "not supported on this OS" error.
  - **`BrowserReceipt`:** a printable page at `/receipts/{paymentId}` (narrow monospace layout, `@media print` CSS) that calls `window.print()` via JS on load. This is the default and works with any printer the desk PC has.
- After a payment, a "Print receipt" button appears in the confirmation, and a reprint is available from /pos history and the customer page. A printing failure shows an error toast and never fails the payment.
- **Settings:** receipt mode, host/port, Windows printer name, columns, header lines (cafe name, address, phone) and footer text, plus a "Print test receipt" button.

### Testing
- EscPosEncoder byte-level tests: init/cut sequences, bold toggling, column wrapping, non-ASCII replacement.
- ReceiptBuilder tests: totals, split payment, change, session + tab combined.
- NetworkEscPosPrinter test against an in-process `TcpListener` capturing the bytes.
- Service tests:
  - Order create/add-to-tab/void.
  - A settle-up allocates correctly across a session and its orders.
  - Outstanding amounts.
  - A partial payment leaves the remainder outstanding.
  - Package sale through the new payment path.
- Page smoke tests: /products, /pos and /receipts/{id} require auth and render.

## Claude's Discretion
- POS visual design (big tap targets, category colors).
- Whether Open tabs is a side panel or a separate tab.

## Deferred
- Inventory/stock, shift cash-up → v2 (out of scope for v1).
- Integrated card payments → out of scope.

## Environment Notes
- Same as earlier phases: `export PATH="/usr/local/share/dotnet:$PATH"`, net10.0, **Microsoft packages only**, EF migrations only via `dotnet tool run dotnet-ef`, commits on main as dylenq with no attribution, and leave the untracked runtime files alone.
