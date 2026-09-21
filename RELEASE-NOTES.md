# Release Notes

Detailed feature history, verification evidence, and the defects found along the way —
moved out of [`README.md`](README.md) to keep it readable. For architecture and the
phased delivery plan, see [`PLAN.md`](PLAN.md).

---

## Scope

### Out of scope for now: fiscal regimes

**No market requiring certified or fiscally-signed documents is targeted yet.** Portugal, Angola and
Mozambique require certified software and cryptographically signed documents; Germany requires a TSE
under KassenSichV. Most markets require none of it.

This is a deliberate exclusion rather than an oversight, and it is the single largest piece of work
between this system and a first paying shop in one of those countries. It is worth being precise about
what it would cost, because it is not a feature that can be bolted on late:

- **Signed documents** need an append-only chain the till cannot rewrite, with each document carrying
  a signature over its predecessor. This design is already append-only and hash-friendly — sales, refunds
  and movements are never mutated, and identity is client-generated — so the chain has somewhere to
  attach. What is missing is the signing step, the key handling, and the export format the authority
  expects.
- **Certified software** means an approval process against a published specification, per country, and
  a certificate number printed on every receipt.
- **TSE** means a hardware security module per till, with its own transaction protocol, and a till that
  must refuse to sell when it cannot reach it.

The reason to say this now rather than later: a fiscal requirement changes the *shape* of the checkout
transaction, not just its paperwork — the signature must be produced before the receipt is printed and
the sale is final. Deciding it after the checkout path has settled would mean reopening the one path in
this system that is most carefully tested.

> **TODO:** pick the first target market and implement its regime end to end. Until then, treat every
> deployment as a jurisdiction with no fiscal requirement, and do not describe this system as compliant
> with any.

---

## Verification and test evidence

### Verified so far

- **1094 tests passing, 0 failures, 0 build warnings.**
  - `Pos.Core.Tests` — 193 tests: tax in both modes, discount allocation, tender validation, split
    tender including the cash-versus-card overtender rule, the refund policy including
    repeated-partial-return abuse, shift cash-up arithmetic, the rule that a mid-shift reading never
    carries the expected drawer figure, consolidated estate reporting, and preparation-station
    routing, and the stock transfer lifecycle including the in-transit state and shortages.
  - `Pos.Devices.Tests` — 300 tests: ESC/POS golden bytes, CP437 mapping, layout, receipt,
    kitchen-ticket, refund, trading-report, period and shift-cash-up rendering, station ticket
    filtering, printer-role binding, ZPL labels and label media geometry, WebUSB and Web Serial
    flows, browser-print degradation, transport fallback ordering, the HTML renderer used by the
    browser-print fallback including that markup in a product name cannot escape into the page, and
    the class-name contract between that renderer and the print frame's stylesheet.
  - `Pos.Infrastructure.Tests` — 285 tests: atomic sale and refund commits, the terminal-sequence
    allocator, the derived stock ledger, stock movements incident to a sale and a refund, outbox
    draining and poisoning, sale and refund numbering and lookup, the sync loop including a
    simulated lost response, trading-report aggregation including refunds and period reporting,
    refund authority, the shift lifecycle end to end, paying-then-recording including a declined
    payment recording nothing, a station surviving the whole chain from catalogue row to
    reprint, the store directory including that it never throws when the hub is unreachable and
    never offers this store or a closed one as a destination, saving a transfer as a draft
    including that it moves no stock and is deliberately not queued for the hub, the sync pass
    running its preflight — where a due credential rotation happens — before it pushes, without a
    failed preflight stopping the push, **the reorder point end to end** from a real sale through the
    movement ledger to the reorder list and back off it after a refund, and **the CSV codec** —
    quoting, header matching by name, percentage tax rates, byte-order marks, and a re-import that
    updates by barcode rather than duplicating the catalogue.
  - `Pos.Sync.Server.Tests` — 74 tests: push idempotency, change-log cursors, tolerance of a
    terminal that restarted its sequence counter, head-office credential handling, consolidated
    report arithmetic from synced payloads, payload indexing, credential hashing, enrolment code
    alphabet, store-id comparison across textual forms, transfer relaying including a payload
    addressed the way a real till writes it, and **credential rotation** — the replacement being
    adopted, the superseded secret working inside its grace window and not after it, an identical
    retry being recognised as the lost-response path, a superseded refresh token minting different
    credentials revoking the device, and every outcome reaching the audit trail.
  - `Pos.Web.Tests` — 242 tests: every screen rendered in-process, and **every pillar of the system
    driven through the controls a shop actually uses** — scanning a barcode into the basket and
    taking payment; finding a sale by the receipt number on the customer's slip and refunding it;
    signing in with a PIN and cashing the drawer up; recording a physical count and a write-off;
    raising, sending, and booking in a stock transfer; and reading the day's trading report. Plus a
    whole sale rung up against a recording device bridge, so what reached each printer can be
    inspected: the receipt and its cut, the drawer kick on cash and not on card, the kitchen ticket
    on the kitchen printer and never on the receipt printer, shelf labels as ZPL, and a sale that
    still completes when the printer fails. Plus customer display projection, label printing,
    catalogue application including the reflection guard, terminal startup across a reload, the
    composition root validated for satisfiability and lifetime, and **the till's half of credential
    rotation** — including that a lost response is retried with the identical replacement rather
    than a fresh one, which is the difference between retrying and being revoked.
  - `Pos.Web.Tests/AccessibilityTests.cs` — 89 of the above, over the markup every screen actually
    renders: **every control has a name**, every button says what it does, every table has named
    header cells, numeric fields a touch till depends on declare their input mode, nothing hijacks
    the tab order, every image describes itself, every screen has exactly one level-one heading with
    no skipped levels, every operator screen has a live region, and **every live region is empty on
    first render**. What that found, and what it deliberately does not cover, are in
    [Accessibility](#accessibility).
- **Every screen is rendered**, not merely compiled. `Pos.Web.Tests` renders all ten pages
  through the framework's own renderer against the terminal's real service graph, so a markup
  expression over a null or a loop over an unpopulated sequence fails in the test suite rather than
  in a shop. The accessibility checks reflect over the assembly for routed components and fail if a
  page is not in their list, so a new screen cannot quietly skip them.
- **Every pillar is driven through its controls** with bUnit: buttons found by label and clicked,
  quantities typed into the boxes, PINs pressed on the keypad, and keystrokes raised the way a
  keyboard-wedge scanner raises them. Assertions are made against the ledger and the stored
  documents afterwards, so what is verified is the behaviour a shop gets rather than the shape of
  the markup. This is what found the defects described below.
- **Sync verified against a running host over HTTP**, not just against helpers: a replayed
  push returns `Duplicate` with an unchanged cursor and the sale appears exactly once in
  the pull. A reused enrolment code is rejected with `400`; a push with no credential is
  rejected with `401`. Transcript in `PLAN.md`.
- **The multi-store loop verified against a running hub over HTTP**, reproducibly, by
  `tools/verify-head-office.ps1`: two stores provisioned and enrolled independently, the
  head-office boundary refusing both anonymous and device-credential callers, a refund and a void
  reported correctly in the estate total, a catalogue published to one store and invisible to the
  other, a transfer relayed to the store it is addressed to with the store ids spelled the way a
  real till spells them, and a cross-store push stored against the pushing store rather than the one
  named in its payload. The store directory is checked to be readable by a device credential, to
  name both branches, and to expose **no** field beyond `id`, `code`, `name` and `isActive`.
  And **credential rotation end to end against the live hub**: a till rotates, the replacement works,
  the superseded secret keeps working inside its grace window, an identical retry is answered as the
  rotation it already applied, and a superseded refresh token used to mint different credentials is
  refused **and revokes the device**. 40 steps, transcript in `PLAN.md`.
- **UI verified rendering in headless Edge** over the DevTools protocol, for the till, the
  refunds screen, and the trading report; and since this round, **every screen** rendered in-process
  by the test suite, which is the check that actually runs on every build.
- **JS shims syntax-checked** with `node --check`, and **every browser module executed** rather than
  read. There are four of them, and between these scripts each is run for real:
  - `node tools/verify-service-worker.mjs` runs the published worker's `install`, `activate` and
    `fetch` handlers in a service-worker scope. 27 checks: the shell, runtime, assemblies, stylesheets,
    icons and globalization data are all cached; source maps and the worker itself are not; an older
    cache is deleted on activate while another app's is left alone; a navigation is served the shell
    from cache **without a network round trip**; an asset is answered from its own cache entry and
    never with the shell document; uncached and non-GET requests fall through; and with the network
    down the shell and cached assets still load while anything uncached fails rather than being
    answered with something wrong.
  - `node tools/verify-device-bridge.mjs` runs the real `device-bridge.js` against a shimmed
    `navigator.usb`, `navigator.serial` and DOM. 53 checks, including the one the whole WebUSB design
    exists for: **`requestDevice()` is called synchronously inside the user gesture**, proven by
    counting the calls before `beginWebUsbRequest` returns. Also the printer-class chooser filter, a
    dismissed chooser being a cancellation rather than an error, silent reattachment reusing one
    handle, interface selection skipping a vendor interface for the one with a bulk OUT endpoint,
    chunked writes reassembling byte-for-byte, an interface held by a system driver being refused
    with advice, and the browser print fallback writing an 80 mm document whose stylesheet defines
    every class the receipt renderer emits.
  - `node tools/verify-local-store.mjs` runs the real `local-store.js` — the production data layer,
    43 KB and 48 exported functions — against an in-memory IndexedDB, and checks 75 invariants:
    the schema and its additive upgrade, terminal sequencing across a reload, the atomic sale commit
    and its outbox, retry-then-park on failure, payload pruning, sale and refund numbering, catalogue
    scoping, shifts and the drawer, transfers, sync cursors, and that a fetched record is a clone.
  - `node tools/verify-display-channel.mjs` runs the customer display channel: what the till sends,
    what the display recognises, and that a browser with no `BroadcastChannel` degrades rather than
    throwing.
- **The published app is verified to be able to boot with no network**, by
  `tools/verify-offline.ps1`. It publishes for real, reads the cache patterns **out of the service
  worker** rather than restating them, and fails if anything needed to boot is not covered by them:
  the shell, the runtime loader and its native and managed parts, every assembly, the app's own
  stylesheets and scripts, the globalization data, the manifest and its icons. Whatever is not
  covered is named and must be source maps. It also asserts the manifest is installable and that the
  app does not still call itself `Pos.Web`. First load measured at **3.06 MB brotli**, cached
  thereafter.
- Toolchain confirmed: .NET SDK **10.0.401**, `blazorwasm --pwa` template builds clean.

---

## Feature checklists, design notes and defect history

### Delivery checklist

| Item | State |
|---|---|
| Domain, tax engine, cart, checkout service | ✅ Done |
| ESC/POS builder, CP437, receipt renderer | ✅ Done |
| Transport abstraction, WebUSB + Simulated, resolver, JS shim | ✅ Done |
| Local persistence, atomic commit, outbox, sale journal | ✅ Done |
| Sync server: schema, enrolment, push/pull, idempotency | ✅ Done |
| Sync client: outbox drain, cursor advance, change applier | ✅ Done |
| Device credential rotation, with reuse detection | ✅ Done, verified over HTTP |
| IndexedDB bridge (`local-store.js`) + browser HTTP transport | ✅ Done |
| Blazor checkout UI — scan, basket, tender, print, drawer, sync | ✅ Done |
| Web Serial transport | ✅ Done |
| Browser-print fallback (Safari/Firefox path) | ✅ Done |
| Kitchen / bar ticket renderer | ✅ Done |
| ZPL label printer + shelf/price labels | ✅ Done |
| Customer-facing display | ✅ Done |
| Device settings: pair a printer, enrol the terminal | ✅ Done |
| Catalogue and stock management screen | ✅ Done |
| Employees, sign-in, shifts, drawer counts, cash-up slip | ✅ Done |
| Split tender and the `IPaymentProvider` seam | ✅ Done |
| Catalogue **pull** from head office into the local replica | ✅ Done, verified over HTTP |
| Multi-day and period reporting; head-office consolidation | ✅ Done, verified over HTTP |
| Web Bluetooth printer | ⬜ Not started — optional, and browser print already covers every browser |

### Reporting

| Item | State |
|---|---|
| Trading-day aggregation (X and Z readings) | ✅ Done |
| Report printing on the receipt printer | ✅ Done |
| Reports screen with an hourly chart | ✅ Done |
| Period reporting (7 / 14 / 30 / 90 days, or a range) | ✅ Done |
| Comparison against the equivalent previous period | ✅ Done |
| Head-office consolidated reporting | ✅ Done, verified over HTTP |
| Refunds shown on the day report and its printed slip | ✅ Done |

Voids are reported **separately** and never netted off the takings. A single netted figure
would hide them, and voids are precisely the number a dishonest till would want hidden. An
X-report states on the printed document that the day remains open, so a mid-shift reading can
never be mistaken for a day close and reconciled twice.

#### Refunds were missing from the shop's own report

The trading-day report read only sales, so a shop's signed Z-report showed takings that ignored
money handed back — while the consolidated estate report *did* subtract them. The two disagreed
about the same day, and the shop's own figure was the wrong one. That is the mirror image of the
problem the estate report was designed to avoid.

Refunds now appear on the day report and on the printed slip, with **refunded tax kept apart from
tax collected** — one is owed onward, the other reclaimed, and a net figure would hide both sides of
a return a tax authority expects to see separately. The slip states **net takings** as its bottom
line. A day with only a refund is no longer described as "no trading recorded", which would have
hidden the one transaction on it worth looking at.

#### A period report, with the gaps kept in

A single day answers "how did we do today" — a question nobody asks on a Monday morning. The period
view covers a range or a 7/14/30/90-day preset, and three decisions make it trustworthy:

- **Days with no trading are listed, not skipped.** A seven-day report covering three trading days
  is a shop that was shut for four of them; compressing the list to three rows makes it read as a
  shop that traded every day for less money. The gaps *are* the information.
- **Two averages, because they answer different questions.** Average per *trading* day is what to
  staff and order against; average per *calendar* day is what to compare against rent, which accrues
  whether the shop is open or not. Confusing them is how a shop talks itself into a bad lease.
- **"Up on last week" is a number or it is nothing.** When the earlier period took nothing, the
  change reads as *nothing to compare against* — not 0%, and not +100%. A shop that opened last week
  has not grown by a percentage anyone should act on.

A backwards range is refused rather than shown as an empty report, because a mistyped date looks
exactly like a period in which the shop took nothing. A range over 366 days is refused too, so a
mistyped year does not read every sale the terminal has ever recorded into memory on a device that
is also trying to serve customers.

### Returns and refunds

| Item | State |
|---|---|
| Refund domain: partial returns, allowance tracking, pricing | ✅ Done |
| Refund policy: over-refund prevention, voided-sale rejection, tender mismatch warnings | ✅ Done |
| Refund document printing | ✅ Done |
| Return persistence: atomic commit, returns store, stock movements | ✅ Done |
| Refund UI on the till — lookup, selection, slip printing | ✅ Done |

A return is its own **append-only record**, never a modification of the sale. The sale is a
historical fact — the customer paid and holds a receipt — so rewriting it would destroy the
audit trail and unbalance the day's takings.

Lines are refunded at **the price actually charged**, apportioned per unit. Refunding the shelf
price after a discount would let a customer profit by buying in a sale and returning later.
Returned goods go back into stock as a **positive** movement referencing the refund, so the
stock ledger stays auditable rather than being edited retroactively.

The refund slip states **"NOT A RECEIPT"** in large type, because a slip that could be mistaken
for a receipt can be presented elsewhere as proof of purchase or used to walk goods out of a
shop. Refund numbers live in a **separate counter** from sales, so one can never be mistaken for
a receipt number. Paying cash for a card sale produces a warning rather than being silently
allowed — a classic fraud route that must always be a deliberate, visible decision.

On the till, a refund starts from the receipt number the customer presents, with the day's sales
as a fallback for a lost slip. Fully refunded lines are dimmed and their input removed, so the
operator sees what is still returnable instead of discovering it when the refund is refused. A
tender mismatch is shown **before** the refund is committed, not after.

### Employees and shifts

| Item | State |
|---|---|
| Employee records, PIN hashing, permissions | ✅ Done |
| Permission flags: sell, refund, void, drawer, discount, close shift | ✅ Done |
| Shift model and append-only drawer events | ✅ Done |
| Cash-up arithmetic: expected cash, variance, no-sale count | ✅ Done |
| Shift persistence: employee roster, shift store, drawer events | ✅ Done |
| `ShiftService`: sign-in, drawer discipline, close and cash up | ✅ Done |
| Sign-in screen with an on-screen PIN pad | ✅ Done |
| Drawer screen: X reading, no-sale, cash in/out, blind count, Z close | ✅ Done |
| Shift cash-up slip (`ShiftReport`) printed on the receipt printer | ✅ Done |
| Roster management (add, edit, deactivate, set PIN) | ⬜ Not started — rosters arrive by sync |

The PIN exists for **accountability, not security**. A four-digit code typed on a shop counter in
front of customers is not a secret in any meaningful sense; it is there so a sale, a refund, or a
drawer opening can be attributed to a person, which is what makes a cash-up meaningful.

Hashing is **salted per employee and stretched** (PBKDF2, 20 000 iterations). Staff routinely share
a PIN like `1234`, so without a salt one leaked hash would reveal that several employees use the
same code and a single precomputed table would break every till at once. A four-digit PIN has only
ten thousand possibilities, so a raw hash is reversed instantly even with a salt. The salt is the
employee id — stable, unique, and needing no extra column.

The sign-in screen is an explicit keypad grid rather than a keyboard listener, and it shows the
digits rather than masking them. Both are deliberate. A keyboard-wedge barcode scanner types into
whatever is focused, so a screen that accepted raw keystrokes could be signed into by a scan from
across the counter — and masking adds a way to mistype the PIN without adding any secrecy.

A shift is the unit of **cash accountability**: every sale, refund, and drawer opening during it
belongs to one person, so a shortage is answerable to a name rather than to a terminal. Totals are
**derived** from the recorded activity rather than accumulated as the shift runs.

Rules the service enforces, each with a test:

- **One open shift per terminal.** Two open drawers on one till would make every cash-up ambiguous.
- **A no-sale opening needs a reason**, and the permission to open the drawer at all. Every no-sale
  opening is the classic cover for a small theft, so these are never deleted or counted down.
- **Cash movements need the same authority as opening the drawer.** Both move money without a sale,
  so a cashier must not reach the same outcome by the unguarded route. This was missing until a test
  made the inconsistency visible.
- **A closed shift accepts no further activity.** Late activity would change a cash-up that has
  already been signed off.
- **Closing someone else's drawer needs the permission *and* the name of who counted the cash**, or
  accountability for a discrepancy disappears exactly when it matters most.
- **A refusal never reveals whether the account exists**, only that the credentials were not
  accepted.
- **A refund needs `Refund` authority.** Refunds are the highest-risk operation at a till — cash
  leaves the drawer and the goods come back — so a cashier who can ring a sale must not thereby be
  able to hand the money back.

#### The count is blind, and that is enforced in the type

`ShiftReport.ExpectedCash`, `.ClosingCount`, and `.Variance` are **nullable, and null for a
mid-shift reading**. A cashier who can see that the drawer should hold 1 240.50 will count to
1 240.50, and the count stops being evidence of anything. Putting the rule in `ShiftReport.From`
rather than in the screen means the printed X-report inherits it too, so a UI that forgot to hide
the figure still could not leak it onto paper.

The X-report still shows the **activity** — takings, the cash/card split, refunds, voids, and the
no-sale count — because a mid-shift reading exists so a supervisor can see the till behaving. Only
the drawer expectation is withheld.

Non-cash tenders are reported but never counted toward the drawer. Card money never entered it, and
a cash-up that included card takings would be over by exactly that amount on every single shift — a
number that is always wrong in the same direction is one everyone learns to ignore.

### Label printing (ZPL)

| Item | State |
|---|---|
| `ZplBuilder`, shelf and barcode label renderers | ✅ Done |
| A third printer role, independently paired | ✅ Done |
| Shelf labels printed from the catalogue screen | ✅ Done |
| Configurable label media size and resolution | ✅ Done |
| Multi-label runs (`copies`) | ✅ Done |

This was the last of the "renderer exists, nothing reaches it" gaps — the same shape as the kitchen
printer was. The builder and both label renderers were written, tested, and completely unreachable:
no label printer role, no label screen, no way for a shop to print a shelf edge.

**ZPL is a third device role, not a mode.** A Zebra label printer and a receipt printer both appear
as USB printer-class devices, so an unbound transport would let them take each other's — and the two
languages are not interchangeable in either direction. ZPL sent to a receipt printer comes out as a
page of literal command text; ESC/POS sent to a label printer produces nothing at all. Keeping them
apart by type is what stops that being a matter of remembering.

**The label role has no browser-print fallback**, for the same reason the kitchen printer does not:
a print dialog opening on the till screen would block the cashier, and the dialog cannot carry a
control language anyway.

#### Media geometry is validated, and "unset" is not the same as "wrong"

A label that is 2mm out does not fit the product it was printed for, and **the printer does not
warn** — it prints a label that looks correct and is the wrong size. So the width, height, and
resolution are checked before they reach the printer, and the two failure cases are treated
differently:

- **Nothing stored** means nothing was ever configured, so the default 50×25mm at 203 dpi is the
  only sensible answer.
- **Stored but out of range** means somebody configured something wrong. The value is surfaced
  rather than quietly replaced, and the printer refuses. Printing a size the operator never chose
  comes out looking like a successful print.

203 dpi is deliberately **7.992** dots/mm, not a round 8. Treated as 8, a 50mm label comes out
50.05mm wide, and the error grows with the label.

### Multi-store and head office

| Item | State |
|---|---|
| Store register: every store, its tills, and their state | ✅ Done |
| Head-office authentication, separate from device credentials | ✅ Done |
| Consolidated estate reporting for a period | ✅ Done |
| Per-store catalogue publishing, pulled by terminals | ✅ Done |
| Stock transfers between stores: document, lifecycle, and ledger | ✅ Done |
| Cross-store relay of transfers over the hub | ✅ Done, verified over HTTP |
| Transfers screen on the till | ✅ Raise, send, and book in |
| Reusable head-office console app | ⬜ Not started — the API is complete; see below |
| Multi-day periods on the till's own report screen | ✅ Done |

#### A transfer is two events, not one

**Dispatching** moves stock out of the sending store; **receiving** moves it into the destination.
They are separate acts, performed by different people at different tills, possibly days apart and
possibly with no network between them. Collapsing them into one would mean either the goods leave
before anyone has packed them, or they arrive before anyone has counted them.

Movement is recorded at **dispatch**, not at creation. A transfer being prepared has not moved
anything, and taking stock off the shelf because somebody opened a form is how a shop comes to sell
something it still has.

While a transfer is in transit the goods are in **neither** store's sellable stock. That is not a
gap between two states — it is what stops both shops selling the same case, and what makes "where
did that pallet go" answerable.

#### The discrepancy is the point

A transfer records what was **sent** and, separately, what was **counted in**. Breakage in transit
is real, and the difference between the two is the number this document exists to produce:

- A transfer recording only what was sent would show stock leaving and never show it failing to
  arrive.
- One recording only what was counted would lose what the sending store put on the van.

Booking in what was *sent* rather than what was *counted* is how a shortage in transit becomes
invisible stock that the destination never finds. Nothing has been counted while a transfer is in
transit, so a discrepancy of zero then means "not counted yet" — not "all arrived", and the two are
distinguishable.

Receiving more than was sent is refused rather than accepted as a surplus: it is a data error, and
allowing it would create stock out of nothing and destroy the meaning of the figure.

#### Rules that stop the expensive mistakes

- **Receiving twice is refused.** A second receipt would append movements for goods that arrived
  once, inflating the destination's stock by the whole transfer — the same rule as closing a shift
  twice.
- **A dispatched transfer cannot be cancelled.** The goods are on a van somewhere; cancelling would
  leave the sending store short with nothing to point at. The way back is to receive it, recording
  whatever did not arrive.
- **Only the destination books goods in.** The sending store confirming its own dispatch would
  defeat the point of two events — nobody would ever be accountable for the gap.
- **An operator cannot send stock from a store they are not signed in at**, so a shortage is
  attributable to somebody who was there.
- **Transferring needs its own permission.** Moving goods between shops is one of the few operations
  that makes stock vanish from one set of books entirely, so it is a supervisor act rather than
  something anyone who can edit a price also inherits.

The document and its stock movements are committed in **one write**, in memory and in IndexedDB
alike. A transfer whose status advanced without its movements would be stock that vanished from the
ledger while the paperwork said it had moved.

**The screen.** `/transfers` is where a shop actually uses this. It shows four things: transfers
**waiting to be booked in**, the form for **sending stock**, what this store has **sent**, and what it
has **received**. Raising and dispatching are the sender's job; booking in is the destination's, and
only appears on the till the hub relayed the document to.

Booking in is built around the ordinary case. An operator who finds everything present leaves every
box blank and clicks once; only someone with a shortage to explain types numbers, and the note is
what makes a shortage actionable later. Each line shows the difference as it is typed, so the
discrepancy is visible before it is committed rather than after.

A discrepancy is reported as a **fact, not an error** — the booking-in succeeded, and the shortage is
the record the whole two-event design exists to produce. Dressing it up as a failure would imply the
goods were not booked in.

#### The branch list a till is allowed to see

A shop cannot address a transfer to a branch it cannot name, and the head-office store register
carries the estate's tax configuration, currency and registration numbers — behind a token that must
never live in a shop's browser.

So `GET /api/sync/stores` is a **separate, device-authenticated directory** returning `id`, `code`,
`name` and `isActive` and nothing else. The projection is the point: widening it is a deliberate act
rather than an accident of returning the entity, and the verification script fails if a field is
added. Enrolled terminals may know which branches exist; they may not know the estate's tax affairs.

The directory is re-read on every visit rather than cached at enrolment, so a branch opened this
morning is transferable to this afternoon. If it cannot be read — hub down, till offline, captive
portal answering with HTML — it degrades to **no known destinations** and the screen says why,
instead of failing the page. Raising a transfer is then impossible, which is honest: the alternative
would be offering a free-text store id that the hub rejects an hour later.

#### How a transfer crosses the store boundary

A terminal's pull returns **its own store's changes and global ones**. That is a security property —
one shop cannot read another's takings — so it must not be relaxed for the sake of transfers.

Instead the hub writes a **second change-log row** for the same record, scoped to the destination
store, and the existing scoping rule delivers it. The sending store keeps its own row, so both ends
see the document and neither sees anything else. Verified over HTTP: after CT01 dispatches, CT01
sees the transfer once and JN01 sees it once — and JN01 still sees **zero** of CT01's catalogue.

Two smaller decisions:

- **The relayed copy keeps the same entity id and type**, so the receiving terminal applies it as
  the transfer it is rather than as a second document for the same goods.
- **A transfer addressed to a store the hub does not know is stored, not refused.** Refusing it would
  strand the record on the sending terminal while the stock had already left that shop's books —
  worse than a transfer that needs looking at by hand.
- **Store ids are compared as identifiers, never as text.** The hub mints them without dashes; the
  till's domain type renders them with, because that is what `Guid.ToString()` does. Comparing the
  strings is what made the relay silently do nothing for a real till — see below.

The receiving store stores the relayed document **without queueing it for upload and without moving
stock**. The goods are still on a van; pushing the document back would have every store accumulating
copies of one transfer.

A late relay is also refused if the local copy has advanced further. The realistic race is that a
store dispatches, the destination receives, and the hub relays the *dispatch* afterwards — a retry,
or a queue that drained late. Overwriting would put the transfer back into transit after somebody
had already counted the goods in, and they would be countable a second time.

#### Two defects the transfers screen exposed

Building the screen was the first thing to exercise transfers **from a real till** rather than from
code or a verification script. It found two bugs that every existing test had passed over.

**A till minted a new store id on every page load.** `TerminalStartup.BuildStore()` called
`StoreId.New()` unconditionally, and nothing persisted the result. Yet the store id keys *every*
local record — `getSalesForDate`, `getEmployees`, `getShifts`, `getStockMovements`, `getProducts`,
and the sale-number sequence are all filtered or keyed by it.

The damage from a single browser refresh:

- The day's **sales disappear** from every report.
- The **catalogue empties and will not re-seed** — the seed probes for a barcode *without* a store id,
  finds the previous store's product, and concludes there is nothing to do. The till shows no
  products and refuses to restore them.
- The open **shift is orphaned**, so a drawer full of cash has no owner.
- **Sale numbering restarts at one**, reissuing numbers already printed on customers' receipts.

It went unnoticed because the terminal id *is* persisted — the code is explicit about why a sync
replica identity must survive a reload — and because the roster re-seeds on every load, which looks
like working sign-in. The store id was simply missed. It is now persisted exactly like the terminal
id, and a reload test starts the terminal twice over one shared local store and one shared browser
storage, which is what a refresh actually is.

**The hub compared store ids as strings.** The hub mints ids as `Guid.ToString("N")` — no dashes. The
till's `StoreId` renders as `Guid.ToString()` — with dashes. `SyncIngest` resolved the relay
destination with `s.Id == destination`, so a transfer from a real till matched no store: it was
accepted, stored, and **never relayed**, leaving the goods in transit forever with nothing on the
destination's screen. The row it would have written also had to carry the hub's spelling, since that
is what the destination's pull is scoped by.

It passed verification because the script sent the payload with the *hub's* own spelling, so the ids
matched by luck. The fix is `StoreIdFormat.Same`, a Guid-aware comparison, plus
`StoreIdFormat.Canonical` for the change-log row. Three things now guard it:

- The verification script sends the payload **the way a till writes it** — dashed — so it reproduces
  the real path. It was confirmed to fail against the old string comparison before the fix went in.
- A server test relays a transfer addressed in the till's format and asserts the row lands under the
  hub's id.
- The same comparison bug was found **a second time** in `StoreDirectory.TransferTargets`, by a test
  written for it — the till would have offered to transfer stock to itself.

Both are the same underlying mistake in different clothes: treating an identifier as text. The rule
now is that store ids are compared as identifiers everywhere, and the hub — which issued them — is
the side that must be tolerant, because a client cannot guess a canonical form its own database is
already full of records contradicting.

#### The defect that only appeared when the button was clicked

"Save draft" on the transfers screen reported **"Raised TR-CT01-JN01-… in draft"** and stored
nothing. `StockTransferService.RaiseAsync` only *builds* the document — the dispatch is what writes it
— so the draft lived in the page's memory and was gone the moment the operator left the screen.

Nothing about it was visible from the outside. The screen compiled, rendered, showed the correct
confirmation, and left the outgoing list empty afterwards in a way that looks exactly like a list
that has not been refreshed. Every service-level test passed, because the services were fine: the
missing piece was a call the page never made.

It is fixed with an explicit `SaveDraftAsync` that writes the draft, and the properties that make
staging safe are now pinned:

- A draft moves no stock — the goods are still on this shop's shelf.
- A draft is **not queued for the hub**. This is the load-bearing one: a draft that synced would be
  relayed to the destination, and somebody would be counting in a van that was never loaded.
- A draft can still be sent later, and sending it is what queues it.
- A dispatched transfer cannot be saved back over its own dispatch.
- Saving a draft needs the same authority as sending one.

The general lesson is worth stating plainly, because it applies to every screen here: **a page is not
tested until something clicks it.** Rendering proves markup is well-formed. Only driving the controls
proves the screen does what it says.

#### The screen that asked a cashier to count a drawer it would refuse

With the technique in place, every pillar was driven through its own controls — and the drawer screen
had the same class of fault as "Save draft", in a form that costs more than lost work.

`ShiftService.CloseShiftAsync` requires the `CloseShift` permission. The screen did not check it. So a
cashier was shown the whole cash-up form, filled in the count, submitted — and was refused *at that
point*, having already counted the drawer. A count that has been done and thrown away is worse than
no count: it is the one figure in the system that is asserted rather than derived, and asking for it
and then discarding it teaches the operator that the screen wastes their time.

The page already gated drawer-opening on `CanOpenDrawer`, so the fix was to apply the rule the file
was already using: `CanCloseShift` hides the form and says who can close instead. Nobody is asked for
a figure they will not be allowed to submit.

That was found by clicking, not by reasoning. The service was correct the whole time, and every
service-level test passed.

#### What the shop actually installs, and what it actually downloads

"PWA" and "offline-first" were in the brief from the start and had never been checked beyond the
plumbing existing. Both turned out to be true in the important sense and sloppy in the visible one.

**The offline half was already sound.** The published service worker is the caching one, its asset
manifest is generated, and everything needed to boot is covered by its patterns. That is now verified
on every run by `tools/verify-offline.ps1`, which reads the cache patterns **out of the worker**
instead of restating them, so editing the worker changes what is checked. It was confirmed to work by
deleting a pattern: six stylesheets went missing from the cache and the check named the first one.

**The visible half was the template.** Installing the till on a shop tablet put a purple .NET "@" on
the home screen under the name **Pos.Web** — the project's name, not the product's. The manifest was
the untouched template default: no description, no scope, no maskable icon, and a `background_color`
of white that disagreed with the dark till, so launching the app flashed white before the first
render. The tab icon was the .NET logo too.

All of that is fixed: `tools/generate-icons.ps1` draws the app's own mark — a receipt on the till's
own surface colour, with a maskable variant that survives Android's cropping — and the manifest now
describes the product. The shell stylesheet also sets the dark background and `color-scheme: dark`,
which the till needed anyway: every number input and select in the app was rendering as a bright
white control on a dark card.

#### The fallback for every other browser printed nothing

The brief asks for a preferred WebUSB transport **and fallbacks for all other browsers**, and the
transports were all there: WebUSB, Web Serial, browser print, simulated. The tests covered each one
and the fallback ordering between them.

What no test covered was a sale. Ringing one up against a recording device bridge showed the receipt,
the cut, the drawer kick, and the kitchen ticket all arriving correctly over WebUSB — and then, with
the device APIs switched off, **nothing at all**.

The browser-print transport is the one that works in Safari and Firefox, where neither device API
exists. It is a chain of three pieces, and all three were disconnected:

1. **No HTML renderer existed.** `PrintHtmlAsync` took a `htmlBody` argument that nothing anywhere
   produced.
2. **Nothing called it.** `ReceiptPrinter` rendered ESC/POS bytes unconditionally and called
   `WriteAsync`.
3. **`WriteAsync` throws by design** — sending control bytes to a print dialog would emit a page of
   command characters — with a message telling the caller to use the method in (1).

So on Safari or Firefox the fallback was selected, then threw, and the checkout screen caught it as a
printer fault. Every sale completed and no customer received a receipt, on the two browsers the
fallback exists for. It is exactly the failure the transports' own tests could not see: every
transport was individually correct, and the seam between them was not there.

The fix is `ReceiptHtmlRenderer` plus an `IHtmlDocumentTransport` interface that `ReceiptPrinter`
routes on — a transport that takes HTML is not a broken ESC/POS printer, it is a different kind of
output. The HTML receipt deliberately mirrors the thermal one, including the rule that a single tax
rate is stated on the total and only a multi-rate sale gets a summary, because a fallback that
printed a visibly different document would be a second document to keep correct.

The renderer escapes every value it is given. Product names, line notes, and the store's footer all
arrive from head office or from an operator's typing, and the result is handed to a browser to
render — so markup in a name would otherwise be injected into the document that gets printed.

**One more silent seam, found while fixing it.** The print frame in `device-bridge.js` supplies its
own stylesheet for the iframe, with its own class vocabulary (`.c`, `.b`, `.s`, `.big`, `.rule`,
`td.amt`). The renderer's first version used different names entirely, so the receipt would have
printed unstyled — readable, but with every price jammed against the item name and the amounts
unaligned. The two files now name each other in comments, and a test reads the stylesheet **out of
the JavaScript** and fails if the renderer emits a class it does not define. Confirmed by renaming one
class: the test named `s` as unstyled and nothing else.

#### The customer display said the till had gone whenever the shop was quiet

The last named peripheral, and the fourth instance of the same shape. The display is a second window
fed over a same-origin `BroadcastChannel`. It runs a staleness timer so that a display which has
silently stopped receiving looks different from one showing an empty basket — and flips to
**"Waiting for the till…"** after ten seconds without a message.

It handles a `heartbeat` message for exactly that reason. **Nothing ever sent one.** The heartbeat
function existed in the channel module, the display recognised it, and no caller was written. So a
till that was working perfectly — a customer browsing, an operator bagging — told the customer it had
gone, ten seconds at a time, all day. A status light that is wrong most of the time is one everybody
learns to ignore, which is worse than not having one.

The till now beats every four seconds, started by the first thing it says to the display and stopped
when the checkout screen is torn down. Stopping matters as much as starting: a heartbeat that outlived
the till would keep a display claiming a closed till was still running.

The channel module itself had also never been executed by anything. It is now verified with Node,
which is enough — it touches no DOM and reaches for `BroadcastChannel` only inside its functions. That
covers what the till sends, what the display recognises, and the case that matters most: a browser
with no channel at all, where every entry point must answer rather than throw.

Also fixed: the publisher's `IsSupportedAsync` probe had never been called by anything either, so an
operator had no way to learn that their browser cannot run a second screen. Device settings now
reports the channel and explains what it means.

#### The tills could charge each other's prices

The whole production data layer — `local-store.js`, 43 KB, 48 exported functions, every sale and
refund and shift the terminal records — had **never been executed by anything**. The C# side reaches
it through `JsLocalStore`, but every test substitutes `InMemoryLocalStore`, so the JavaScript was
verified by reading it and by a hand-written mirror agreeing with a contract somebody wrote by hand.
The worst data-integrity bug this project has had lived in that file.

It now runs under Node against an in-memory IndexedDB (`tools/verify-local-store.mjs`), with no
package and no install step, and 74 invariants checked. Running it found two defects immediately.

**Scans and searches were not scoped to a store.** The local catalogue holds one row per store per
product, and a chain sells the same barcode in every branch — so `findProductByBarcode` resolved to
whichever row the index returned first. A till in one shop could scan a tin and be given *another
shop's row, with that shop's price and tax*, and charge it. The name search was worse: it filtered by
neither store nor deletion, so a product head office had deleted stayed sellable through it, on the
one path that exists for labels which will not scan.

It was an interface-level omission, not a slip: `FindProductByBarcodeAsync(barcode)` and
`SearchProductsByNameAsync(term)` had no store parameter at all, and the in-memory mirror had the
same gap — which is exactly why the mirror never caught it. Both are now scoped, and the barcode
index is queried with `getAll` rather than `get`, because the barcode genuinely is not unique across
the store any more.

**Every record ever committed stayed in IndexedDB.** The `payloads` store holds a full serialised copy
of each sale, movement, refund, shift, drawer event, and transfer. `acknowledgeOutbox` deleted the
outbox entry and left the payload behind, and nothing else ever deleted one — so the largest store in
the database grew for the life of the till. A shop that fills its storage quota loses its trading,
which is the single outcome the local store exists to prevent. Payloads are now released in the same
transaction as the acknowledgement, keyed off the outbox row so a movement's payload is not taken with
its sale's.

#### The WebUSB handshake, run for the first time

The same mirror problem applied to `device-bridge.js`. The C# side is tested against
`FakeDeviceBridge`, written to agree with the real module, so anything both got wrong was invisible
from either side. It holds the most intricate logic in the system — the deferred-promise handshake
that exists because `navigator.usb.requestDevice()` only works while a user gesture is still live.
That constraint has been documented since the first design note and had never been executed.

It is now run under Node against a shimmed `navigator.usb`, `navigator.serial` and DOM, and the
constraint is the first thing asserted: **`requestDevice()` is called synchronously**, proven by
counting the calls before `beginWebUsbRequest` returns rather than by trusting the comment. Deferring
that call by even one microtask — the tidy-looking change a reviewer might suggest — makes that check
fail and nothing else, which is how it was confirmed to be worth having.

**This one found no defect**, and that is worth stating plainly rather than dressing up. It covers the
printer-class chooser filter, a dismissed chooser reported as a cancellation rather than an error, a
granted device reattaching without a prompt while reusing one handle, interface selection skipping a
vendor interface to find the one with a bulk OUT endpoint, a claim refused by a system driver being
reported with advice to try serial instead, chunked writes reassembling byte-for-byte, and closing an
already-unplugged device not becoming an error. All of it was correct. The value is that it is now
*known* to be correct rather than assumed.

It also closes the loop on the print fallback rebuilt two rounds ago: the browser print path writes an
80 mm document, and a check reads the stylesheet it actually writes to confirm every class the receipt
renderer emits is defined there — the agreement that had broken silently before.

#### The service worker, which decides whether a shop can open at all

With the data layer and the device bridge executed, one browser module was left unrun: the service
worker. The offline check had been reading its cache patterns out of the source and reasoning about
the manifest — a contract check, and a good one — but the handlers themselves, the code that decides
what a browser gets when a shop's internet is down, had never run.

It runs now, in a service-worker scope built on `node:vm`, and the branch worth naming is in the fetch
handler: a navigation is served `index.html` from the cache **unless the requested URL is itself one of
the manifest's assets**. Serving an HTML document in reply to a request for a `.wasm` file fails in a
way that looks like nothing at all. Replacing that substitution with a plain pass-through — the
obvious simplification — fails two checks and then makes the offline shell test throw, which is how it
was confirmed to be load-bearing.

**This one found no defect either.** What it establishes is the sequence a shop actually lives
through: the worker installs while online and caches the shell, the runtime, every assembly, the
stylesheets, the icons and the globalization data; source maps are skipped and so is the worker
itself, because a cached copy of a running worker is a terminal stuck on an old build forever; an
older cache is cleared on activate while another app's is left alone; a navigation afterwards costs no
network round trip; and once the connection goes, the shell and every cached asset still load while an
uncached request fails honestly rather than being answered with something plausible.

Two of my own modelling errors surfaced doing it, and both were the shim being *less* faithful than a
browser in a way that disguised the answer. `cache.match('index.html')` resolves relative to the
worker's script URL, so a stub comparing it literally reported a worker that never serves the shell
offline. And `cache.addAll` is not bookkeeping — it fetches, and rejects the batch if any fetch fails,
which is what makes a worker refuse to install when it cannot cache what it needs. Fixing the second
also corrected the scenario: a worker installs **while online** and only then loses the connection, so
the harness now models that order rather than starting offline and reporting success it could not
have had.

The two service-worker checks are deliberately split. `verify-offline.ps1` needs a real publish and
checks the shipped manifest against the worker's patterns; `verify-service-worker.mjs` needs a VM and
checks behaviour against representative data. Merging them would make the slow one the only one
anybody ran.

**And 8 MB of framework CSS that nothing used.** The template ships the entire Bootstrap
distribution: twenty stylesheets including the RTL and unminified variants, six scripts, and
twenty-two source maps. Not one Bootstrap class appears in any markup — the till defines its own
`.btn`, `.badge`, and the `.pos-*` / `.dev-*` / `.cd-*` families — so it was dead weight that lost
every specificity contest to `pos.css` and made "why does this button look like that" a live
question. `sample-data/weather.json` from the template was shipping too.

Removing it took the app's asset manifest from 114 files to 70, removed the source maps entirely, and
took the deployment from 28 MB to 20 MB. The first load barely moved — 3.33 MB to 3.06 MB — because
Bootstrap's minified CSS was only a small part of the compressed payload. That is worth stating
plainly rather than claiming a speed-up: the win is in what the app *is*, not in how fast it starts.

#### A catalogue pull replaces the local row, which makes an incomplete payload destructive

The terminal's catalogue is a read-only replica, so applying a change **overwrites** the stored
product. Every field the applied payload does not carry is a field the sync silently erases — no
error, no warning, and the till keeps selling. The thing that was lost only shows up later, as work
that never reached the kitchen.

That is not hypothetical. The preparation station was added to the stored product for the kitchen
printer, and `CatalogChangeApplier` — which listed the fields by hand, written before stations
existed — did not know about it. The next catalogue sync would have stripped the station from every
product in the shop, and the kitchen would simply have stopped receiving tickets.

The fix is structural rather than a patched list:

- **One payload type, shared by both ends of the wire.** `CatalogProductPayload` is what the hub
  publishes and what the terminal applies. Two hand-written lists of the same fields is two lists
  that drift; a single definition cannot.
- **A round-trip test** builds a product with every field set to something distinctive, sends it
  through the applier as the hub would, and asserts the stored row matches.
- **A reflection guard** compares the two shapes and fails the moment a field is added with nowhere
  to travel. The round-trip test alone is not enough: add a field, leave the test alone, and it
  passes because both sides are at their default. The reflection guard cannot be forgotten.

Both guards were verified by reintroducing the defect and confirming they fail — the round trip
reports `StationId = kitchen` becoming `StationId = `, and the reflection guard names the field.

### Head office and device credentials

#### Two credentials, and why one would not do

A **device credential** is scoped to exactly one store and lives on a till in a shop that may not
be physically secure. That is the whole point of it: if a terminal is stolen, the blast radius is
one store's books.

The authority to create stores, mint enrolment codes, and revoke other terminals therefore cannot
live on a device at all — an enrolment code is the *bootstrap* for a device credential, so a
terminal able to mint one could enrol an unlimited number of its own devices and write into the
estate indefinitely.

So head office has its own token, supplied out of band through configuration and never stored in
the database. A dumped database yields nothing that can provision anything.

**When the token is absent, every head-office endpoint refuses everything.** Failing open would be
the worst of both worlds: a hub that looks configured, runs happily, and silently hands out device
credentials to anyone who asks. The two refusals are also deliberately different — `503` for "this
hub has no head-office token set" and `401` for "the token you sent is wrong" — because the caller
here is an operator standing up a hub, and a bare "not authorised" would send them hunting for a
typo in a token that was never configured. That is the opposite of the device-enrolment refusal,
which is deliberately vague so that enrolment codes cannot be probed.

> **This is why the head-office console is not a page in the till.** Putting the head-office token
> into a terminal's browser storage would hand every till in the estate the authority to provision
> stores and revoke other terminals — undoing the separation entirely. A console belongs on its own
> origin with its own credential, and until it exists the API is the interface.

#### The device credential rotates, and why the till generates the replacement

A credential that can never change is valid forever the moment it leaks, and the device secret travels
on every sync request — so it is the value most likely to end up in a proxy log, a crash dump, or a
browser profile copied off a stolen till. Every credential therefore carries a **refresh token** and a
due date, and the till replaces the pair when the date passes.

The replacement is generated **on the till**, not by the hub, and that single decision is what makes
the whole thing survivable over a shop's connection:

| Ordering rule | Why |
|---|---|
| **Written down before it is sent** | The pending pair is persisted first, then the request goes out. A browser closed mid-request leaves the till holding the exact pair it asked for. |
| **The credential in use changes only on confirmation** | Until the hub answers, the till keeps authenticating with what the hub already knows. A failed rotation is not a failed sale. |
| **A retry is the identical request** | Because the till authored the pair, retrying means sending the same two values. The hub recognises it and answers as the rotation it already applied. |
| **A different pair on a superseded token revokes the device** | Two parties hold this device's credentials. Only one of them can be the till. |

That last rule is the security payoff: rotation is not really about the secret expiring, it is about
**noticing when a credential is in two places at once**, which is otherwise invisible. It is also why
the other three matter — a till that generated a *fresh* pair on each attempt would present a
superseded refresh token with different credentials, which is exactly what a thief would look like, and
it would revoke itself.

The timings are deliberately lopsided: the credential is due after **7 days**, and the superseded
secret keeps working for **14 days**. The grace window is not a security feature — it is the price of
rotating over a connection that drops. A shorter window is a shorter exposure and a taller pile of
tills that have to be enrolled by hand after a bad afternoon's wifi.

**What rotation does not buy, stated plainly.** The refresh token is not a second factor: it lives in
the same browser storage as the secret, so anybody who can read one can read both. Against an attacker
with the till's storage, rotation buys a bounded lifetime and a loud audit trail, not prevention. What
it does defend is the secret that leaked *without* the storage — a log, a dump, a shared screenshot —
which is the realistic case, and it turns that from permanent access into fourteen days and a
detection.

**A terminal enrolled before this round holds no refresh token.** It keeps trading and syncing, and
cannot rotate until it is enrolled again. The device settings screen shows the credential's state, so
"due now" and "waiting for the hub" are visible rather than silent.

##### Upgrading a hub that already exists

The hub creates its schema with `EnsureCreated`, which never alters an existing table. A hub that has
been running before this round needs four columns added, so it refuses to start and prints exactly
what to run rather than failing later on every authenticated request:

```sql
ALTER TABLE devices ADD COLUMN PreviousSecretHash TEXT NULL;
ALTER TABLE devices ADD COLUMN PreviousSecretValidUntil TEXT NULL;
ALTER TABLE devices ADD COLUMN PreviousRefreshTokenHash TEXT NULL;
ALTER TABLE devices ADD COLUMN CredentialIssuedAt TEXT NULL;
```

Deliberately not automatic. Additive `ALTER TABLE` statements would be easy here, and hand-rolled
migrations are the thing that stops being easy later; an operator upgrading a live hub should do it
deliberately, with a backup.

#### Ownership removes the conflict instead of merging it

| Entity | Owner | Technique |
|---|---|---|
| Sales, refunds, stock movements, shifts | Terminal | Append-only, deduped by client-generated id |
| Catalogue, prices, tax | Head office | Single-writer; the terminal is a read-only replica |
| Store price override | That store | A separate entity, never a mutation of the head-office row |

The catalogue is *published* by head office and *pulled* by terminals over the ordinary catalog
stream. Head office never writes into a terminal and a terminal never writes the catalogue, so
there is no merge and no conflict to resolve — and no separate "online" code path to get wrong.

Each publication is scoped to one store, because a price list is a per-store artefact in a
franchise and a global publication would silently overwrite pricing a store had negotiated.
Rejections are per product: one bad line on a price list does not stop the other four hundred
products reaching the shop.

#### The estate total and the shop's own Z-report must agree

A consolidated report is built from the records the hub holds, so it is unaffected by a till that
never printed its Z-report. But it follows the **same rules** as the shop's own report, because a
franchise total that disagreed with the shop's own Z-report would be worse than no total at all:

- **Voided sales are counted and valued separately, never netted off the takings.** Netting them
  would misstate both the takings and the void count.
- **Refunds reduce net takings, not gross.** Gross is what was rung up; net is what the estate kept.
- **Refunded tax is reported separately from tax collected.** Tax collected is owed onward and tax
  reversed is reclaimed; a net figure would hide both sides of a return that a tax authority
  expects to see apart.
- **An idle store is kept in the report, not dropped.** A store that recorded nothing is either
  closed or has stopped syncing — and those need completely different responses from whoever is
  looking. A report that silently omitted it would hide both.

### The composition root is tested, because a browser was the only thing checking it

The registrations used to be top-level statements in `Program.cs`, which meant the only thing that
ever resolved them was a browser loading the page. That is a slow and unreliable detector. A missing
registration, a wrong keyed lookup, or a singleton depending on a scoped service all fail at **first
resolution** — which in a shop is the first sale of the morning, and in this project was a screen
nobody had opened.

It grew steadily while untested: identity, two stores, three printer roles, two payment services,
four recording services, sync, and a keyed resolver per role. So it now lives in
`TerminalServices.AddPosTerminal`, which `Program.cs` calls in one line and a test can call too.

The container is built with `ValidateOnBuild` and `ValidateScopes`, and then **every registered
service is resolved**. The second part matters: validation proves the graph is *satisfiable*, but a
factory that throws — a bad cast, a keyed lookup for a key nobody registered — passes validation and
fails on first use.

The sharpest assertion is that the kitchen and label printers are wired to **their own** role. Both
are thin wrappers that resolve happily whichever provider they are handed, so a copy-paste key in
the container passes every other test in the suite, and sends ZPL to the pass or food orders out of
the customer's receipt roll. `KitchenTicketPrinter.Role` and `LabelPrinter.Role` exist so that is
assertable rather than assumed.

All of it was checked by breaking the container deliberately and confirming the tests fail:

| Breakage | Result |
|---|---|
| Drop the `IPaymentProvider` registration | 3 tests fail |
| Point the kitchen ticket printer at the label role | 1 test fails |
| Make `SaleCompletionService` a singleton over a scoped recorder | 2 tests fail |

**What this does not prove.** It shows the service graph is sound and every service constructs. It
does not show that any screen renders, and it does not replace opening the app — the sign-in,
drawer, and catalogue screens in particular are still compile- and unit-verified only.

### Payments and tender

| Item | State |
|---|---|
| Split tender: cash, card, voucher, gift card, loyalty | ✅ Done |
| `IPaymentProvider` seam, for an integrated terminal later | ✅ Done |
| A declined payment blocks the sale being recorded | ✅ Done |
| Refunds, exchanges, and refund authority | ✅ Done |
| Integrated card terminal | ⬜ Needs hardware — the seam is ready for it |

Two claims here were false until this round. `IPaymentProvider` **did not exist anywhere in the
codebase** despite the plan marking the phase complete, and split tender was unreachable: the
domain accepted a list of tenders, but the till passed exactly one, so a cashier could not take
£30 cash and £20 on a card.

#### Cash and everything else overtender differently

This is the rule that makes split tender more than addition:

- A customer handing over **100 for a 70 bill is normal** and produces 30 change. What is *applied*
  is 70; what was *handed over* is kept alongside it, so the change is recoverable from the record.
- A customer "paying" **100 by card for a 70 bill is not change** — it is a cash advance. Recording
  it would book 30 of income the shop never earned, and the excess would have to come back as a
  refund. So every non-cash method is capped at what is still owed.

`TenderSession` owns those rules in the pure domain. It also refuses to produce tenders unless the
balance is settled exactly, which is what stops an under-paid basket reaching the recorder.

**The basket locks once money is taken.** Adding or removing items against a part-paid basket would
re-price something the customer has already paid towards, without telling them. The till refuses the
edit and says why.

#### Pay first, record second

`SaleCompletionService` secures every payment and only then writes the sale. A sale recorded against
a payment that later declined is a sale the shop cannot collect on, and by then the customer is
walking out with the goods.

A decline is an ordinary retail outcome, so it gets its own exception type rather than sharing one
with programming errors — catching the two together means one of them is always handled wrongly. On
a decline nothing is recorded at all, not even the payments that were already authorised: a
partially authorised sale is worse than none, because the shop holds money for a sale that does not
exist.

#### The record-only provider is honest about what it cannot do

The system ships one provider, for a shop with a standalone card machine: the operator runs the card
and tells the POS it went through. It **approves everything, because it has nothing to check**.

That is not papered over. `CanDecline` is `false` and says so, so the interface never implies a
verification that did not happen. A shop that wants the till itself to refuse a bad card needs an
integrated terminal — a hardware change, not a configuration one — and the seam is where it plugs
in. Cash is never sent to a provider at all: a cashier cannot decline a note they are holding.

### Kitchen and bar printers

| Item | State |
|---|---|
| Station routing: one ticket per station, by product | ✅ Done |
| Station carried from catalogue to sale to reprint | ✅ Done |
| A second, independently paired printer device | ✅ Done |
| Binding so two printers cannot take each other's device | ✅ Done |
| Station picker on the catalogue screen | ✅ Done |

The earlier entry here claimed "station routing ✅ Done" when only the *renderer* existed — a
`station` parameter that changed a heading and nothing else. No product could be assigned a
station, nothing split a sale, and there was no second printer to send anything to. It is real now.

#### One ticket per station, not one ticket per sale

A shop has more than one place food and drink are made, and the people in those places need
different pieces of paper. A barista does not need the burger and the kitchen does not need the
wine — and printing everything everywhere means every station reads past most of what it is
handed, which is exactly how a real item gets missed.

Three rules, each with a test:

- **A line with no station produces no ticket.** A bottled drink is handed over at the till.
  Sending it to a kitchen printer wastes paper and, worse, trains the kitchen to ignore tickets.
- **A voided sale produces nothing at all.** The goods were never made, and a ticket for a reversed
  sale is a real cost: somebody cooks food nobody pays for.
- **An unknown station id still gets a ticket**, printed under its raw id. A product pointing at a
  station the store has not declared is a configuration gap, and dropping its line silently is the
  one outcome that loses a customer's order.

The station is carried from the catalogue row onto the cart line, the sale line, and the stored
line — so a reprint routes the way the product was configured **when it was sold**. A product moved
from the kitchen to the bar next week must not change where a reprint of last week's order goes.

#### Two printers, and why they must not take each other's device

The receipt printer and the kitchen printer are separate physical devices. A transport with no
binding attaches to the first authorised device it can open, so with two roles resolving at startup
they fight over one printer — and the symptom is not a failure. It is the kitchen ticket coming out
of the receipt printer, which looks like it worked.

So a transport is constructed with a `PrinterBinding`, in one of three states:

| State | Meaning |
|---|---|
| **Bound** | Paired to one specific device, and may use only that one |
| **Unpaired** | Never paired; attaches to nothing, so it cannot re-point the other role |
| **Any** | No role separation — the single-printer behaviour |

A bound transport deliberately **does not hunt for a working device**. If the kitchen printer is
unplugged, quietly printing its tickets on the customer-facing roll is worse than not printing
them: the operator sees paper come out and has no reason to look closer.

#### The kitchen printer has no fallback, on purpose

The receipt printer degrades all the way down — WebUSB, Web Serial, and finally the browser's print
dialog — because a sale the customer cannot prove is not a completed sale.

The kitchen printer stops at Web Serial. Adding the browser dialog as a fallback would open a print
window **on the till screen in the middle of a sale**, blocking the cashier from serving the next
customer in order to produce a ticket nobody asked for. A shop that has no kitchen printer should
simply have no kitchen tickets.

This is why `TerminalPrinterProvider.GetAsync` returns a nullable printer while `RequireAsync`
returns a guaranteed one: the two roles make different promises, and each call site now states
which one it is relying on.

### The terminal sequence, and two defects it exposed

Every record a till authors takes a `terminalSeq` from **one persisted counter**, reserved in
blocks. Sales, refunds, stock movements, shifts, and drawer events all draw from it, because they
share a single ordered stream.

Both halves of that are load-bearing, and each was a real defect before it was fixed.

**Two counters meant two records at one position.** `CheckoutRecordingService` and `ShiftService`
each kept their own in-memory counter, both starting at 1. On a single fresh page load with no
refresh at all, sale #1 and the shift's opening drawer event were both position 1. The hub has a
**unique index on `(terminalId, terminalSeq)`** as a second line of defence against a record
arriving twice under different ids — so the second record to arrive was refused.

**An in-memory counter restarts on reload.** A browser refresh put the counter back to 1, so the
first record pushed after a reload collided with one already stored. Because a push is a **single
transaction**, that collision failed the *entire batch*, and the terminal retried until the entry
was parked as dead. A legitimate sale was stranded on the till, permanently.

The fix has two halves, because either alone would be incomplete:

- **The terminal** persists the counter in IndexedDB and reserves a block per commit, so a sale and
  its stock movements take consecutive positions and a drawer event cannot land on the same one.
  Reserving exactly the right size keeps the stream gap-free — a gap is the hub's evidence that
  records were lost, so it must not be produced casually.
- **The hub** absorbs a reset instead of refusing it. A position at or below the highest already
  stored means the terminal restarted; it is given the next free position. A **forward** jump is
  still logged as a gap, because that is what the field is genuinely for. Reassigning rather than
  ignoring is also what stops a restart from *looking* like a gap, which would raise a false alarm
  on every refresh — and an alert that fires constantly is one nobody reads.

The sequence number is ordering metadata; the entity id is the identity. Losing a real sale to
protect a position marker is the wrong trade.

Ingest also had to be **extracted from the endpoint delegate** into `SyncIngest` to be testable at
all. The tests previously asserted a hand-written *copy* of the rules, which is a test of the copy:
it would have stayed green while the endpoint diverged. `SyncSequenceLedger` carries the per-batch
state, because a record added to the change tracker is not yet visible to a query — so two records
in one batch that both needed reassigning would otherwise have been handed the same position,
reintroducing the very collision the logic exists to absorb. The same gap let a record repeated
within one batch fail on the primary key.

**A third defect, found by the same test.** `InMemoryShiftStore` claimed to mirror the browser
implementation "so the shift lifecycle can be exercised end to end on a build agent with no
browser" — but its sales and returns were only ever populated by a test-only `AttachSale` helper.
A sale committed through the real checkout path was invisible to the cash-up, so the shift lifecycle
could pass every test while the cash-up was wrong in the only place it matters. It now links to the
ledger, and a test rings a sale through the actual checkout and finds it in the shift's cash-up.

---

## Accessibility

A till is not a form somebody fills in once. It is held at arm's length under bad lighting, by
somebody who is also handling goods and talking to a customer, for eight hours. An unlabelled box or
an unreadable table is a daily irritation for every operator and a barrier for some of them, so
`Pos.Web.Tests/AccessibilityTests.cs` checks the markup the screens actually render — the same DOM a
browser gets — rather than trusting the source to look right.

**Eighty-nine checks over ten screens, and the first run failed fifteen of them.** Every failure was
real. They came in four kinds, and the last of them is the one that looks cosmetic and is not: a
screen with no headings is a screen that has to be read from the top, every time.

| Defect | What it was | Fix |
|---|---|---|
| **The status line was never announced** (all 8 operator screens) | Every page wrapped its message in `@if (_message is not null)`, so the live region was *created* at the same moment as its content. A screen reader announces a region when its contents change; content that arrives with the region is not a change. The till's whole feedback model — "Added Cola 500ml", "Sale CT01-… recorded", "A reason is required to open the drawer without a sale" — was silent to anybody not watching the screen. | One `Layout/StatusMessage.razor`, always rendered and empty, that the message is written into. |
| **Unlabelled controls** (checkout, catalogue, refunds) | `<input id='' type='text'>` — the scan box, the catalogue search, the receipt lookup. Repeated rows are where this goes wrong, because a `<label for>` needs a unique id and omitting it is the easy way out. | `aria-label` on each of the three. |
| **Checkout had no headings at all** | Nothing to navigate by; the other screens started at `h2`, announcing themselves as a fragment of some other page. | A visually hidden `h1` per screen. The status bar was left alone rather than restyled: its layout is deliberate and already says the store, the terminal and the operator. |
| **The shift tables had no header cells** | `<table><tbody><tr><td>Sales</td><td>412.50</td>` reads out as a stream of unlabelled figures. | `<th scope="row">` on the labels, with the stylesheet resetting weight and alignment so a label column does not render as a column of bold headings. |

Two judgement calls came out of it, and both are written into the test file as decisions rather than
left as gaps:

- **`role="status"` with `aria-live="polite"`, not `role="alert"`.** The operator is mid-task with a
  customer in front of them; an assertive interruption on every scanned item would talk over the
  thing they are trying to do. Errors are distinguished visually, and the announcement waits its
  turn.
- **The 404 screen and the customer display have no live region, deliberately.** Nothing on the 404
  ever changes. The customer display is a mirror of the till, read by somebody standing in front of
  it rather than by the operator, and every change it shows was made and announced on the till; a
  live region of its own would double-announce the basket to anyone running a screen reader on the
  machine driving the till.

Two further cases were found by turning the first finding into a rule — *a live region must be empty
on first render, because text that is already there is never announced*:

- The transfer screen's "moving stock needs a supervisor" notice was a `role="status"` region that
  was always full from the first render. It is a condition of the page rather than an event, so it is
  now plain text, read in document order.
- The PIN pad's live region announced nothing at all, because the four dots say how far along the PIN
  is through CSS classes on empty elements and an empty element has nothing to announce. It now
  carries a visually hidden word count — the *count*, never the digits, which would put the PIN in
  the room and in the accessibility tree.

**Not covered, and not claimed:** contrast ratios, focus order and focus visibility, reading order as
a screen reader linearises it, target sizes, and behaviour under browser zoom or with a screen reader
running. Those need a browser and an assistive technology; the checks here are structural, over the
DOM, and they run on every build because of it. Also not covered: the receipt and kitchen ticket as
*paper*. The print path is verified byte-for-byte and its HTML fallback is verified, but a printed
80 mm slip is a physical artefact and reading one aloud is a different problem from reading a screen.

### A component parameter without `@` is a string literal

`<StatusMessage Message="_message" IsError="_messageIsError" />` compiles, runs, and renders. It also
binds the *string literal* `"_message"`, because a component parameter written without `@` is a
literal when the parameter is a `string` — while a `bool` parameter in the same tag is parsed as C#,
which is what makes the mistake so convincing: the error styling worked while the text was the
placeholder. The screen showed the words `_message` where a message should have been. Every component
parameter in this codebase is therefore written `Message="@_message"`, and seven existing tests that
assert a *message* appears through the controls caught it within a minute of the first run — the
accessibility checks could not, because they assert structure, not content.
