# SmartQ Blazor POS — Architecture & Delivery Plan

**Status:** Proposed, awaiting sign-off
**Author:** DSH agent
**Date:** 2026-03-25

---

## 1. What you asked for

| Decision | Your answer |
|---|---|
| Browser target | Must work in **any** browser (incl. Safari/Firefox). **Prefer WebUSB** first, since Chromium/Edge has by far the largest market share. Device APIs are **fallback-only**. |
| Stack | **Blazor / .NET** |
| Backend | Local-first PWA, offline-capable, no server |
| Feature depth | Full retail suite |
| Scale | Multi-store / franchise |
| Payments | Record-only tender now, seam for integrated terminal later |
| Peripherals | Thermal receipt printer (ESC/POS), cash drawer, kitchen/bar printer, label printer (ZPL or ESC/POS label), customer-facing display |

---

## 2. Two contradictions I need to resolve with you

These are not nitpicks — each one would cost a rewrite if we get it wrong.

### 2.1 "Blazor" + "no server" + "offline" ⇒ must be Blazor **WebAssembly**, not Blazor Server

You picked *local-first PWA, offline-capable, no server*. Blazor **Server** holds a SignalR circuit to the server; if the network drops, the UI dies mid-sale. That is disqualifying for a POS.

**Resolution: Blazor WebAssembly Standalone (PWA).** Real C#/Razor, runs entirely in the browser, installable, genuinely offline. *Verified working on this machine:* .NET SDK 10.0.401, template `blazorwasm --pwa` builds clean (`0 Warning(s), 0 Error(s)`).

⚠️ **Consequence:** `.NET MAUI Blazor Hybrid` is off the table too, since it is not a browser.

### 2.2 Multi-store + franchise + no server ⇒ data is stranded per terminal

With no server, store A's sales cannot reach store B, and there is no consolidated reporting, shared catalog, or price list. Offline-first and *zero-infrastructure* are compatible; offline-first and *multi-store consolidation* are not.

**Resolution — proposed:** keep the **no-server deployment** (so it runs today, offline, with zero infra) but build an explicit **`ISyncSource` seam** and a store-scoped data model from day one. Phase 6 adds an *optional* small sync service that you can host later without touching domain logic. You get a working POS immediately and multi-store consolidation when you want it.

**I need you to confirm this trade — it is the single most consequential decision here.**

---

## 3. Verified technical findings

I checked these rather than assuming; they drive the design.

### 3.1 WebUSB is Chromium-only (~77.9% global)

| Browser | WebUSB |
|---|---|
| Chrome 61+ | ✅ |
| Edge 79+ | ✅ |
| Opera 48+, Samsung Internet 8.2+ | ✅ |
| Chrome for Android 152+ | ✅ |
| **Safari (desktop + all iOS)** | ❌ never |
| **Firefox** | ❌ never — Mozilla's official standards position is [**"harmful"**](https://github.com/mozilla/standards-positions/#webusb) |

`WebHID` is [flagged **experimental / non-Baseline** by MDN](https://developer.mozilla.org/en-US/docs/Web/API/WebHID_API) — same Chromium-only story.

**Conclusion:** "prefer WebUSB, fall back for others" is exactly right, and it is only achievable behind a **transport abstraction**. WebUSB must be *one adapter*, never the foundation. This is the core architectural decision of the build.

### 3.2 Two WebUSB constraints that shape the interop layer

1. **HTTPS + user gesture required.** [`navigator.usb.requestDevice()` may only be called through a user gesture](https://developer.chrome.com/docs/capabilities/usb), and WebUSB requires a secure context. A Blazor `async` handler that `await`s before calling it **has already lost the gesture**.
2. **Permissions persist, prompts do not.** [`navigator.usb.getDevices()` returns all devices the origin has already been granted](https://developer.chrome.com/docs/capabilities/usb) — **without a prompt**, so a terminal auto-reconnects its printer after a one-time pairing. This is what makes the design deployable rather than a daily re-pairing chore.

**Resolution:** Blazor WASM supports **synchronous** JS interop via `IJSInProcessRuntime`. The pairing button calls `requestDevice()` **synchronously inside the gesture**; every later reconnection goes through `getDevices()` with no prompt at all.

### 3.3 Scanners are mostly a solved problem

Nearly all handheld scanners ship in **HID keyboard-emulation ("keyboard wedge")** mode, which types into a focused input and works in **every** browser with **zero APIs**. That is the universal baseline. `WebHID` is then an *enhancement* for Chromium only: software-triggered scanning, beeper/LED control, and — importantly — preventing scanner keystrokes from leaking into unrelated fields.

### 3.4 Toolchain constraints on this machine (already handled)

- `dotnet` CLI needs `DOTNET_CLI_HOME` redirected, as the sandbox blocks writes to `C:\Users\Jawid Hassim\.dotnet`.
- Outbound TLS is blocked under `workspace-write`; **NuGet restore needs an escalation** (`SEC_E_NO_CREDENTIALS`). The package cache is now primed, so normal builds work offline.
- `dotnet workload list` errors — a sandbox artifact. Plain Blazor WASM does **not** need `wasm-tools` (only AOT/relinking does). Confirmed by a successful build.

---

## 4. Architecture

### 4.1 Layering — the whole point is testability without a browser

```
┌──────────────────────────────────────────────────────────────┐
│ Pos.Web          Blazor WASM PWA — Razor UI, DI, routing      │
├──────────────────────────────────────────────────────────────┤
│ Pos.Devices      Transport adapters (WebUSB/Serial/BT/Bridge) │
│                  + ESC/POS & ZPL emitters                     │
├──────────────────────────────────────────────────────────────┤
│ Pos.Infrastructure   IndexedDB persistence, sync seam         │
├──────────────────────────────────────────────────────────────┤
│ Pos.Core         PURE C# — domain, pricing, tax, cart, tender │
└──────────────────────────────────────────────────────────────┘
        wwwroot/js/*.js   thin JS shims (only what C# cannot do)
```

**`Pos.Core` has zero browser dependencies.** Cart math, tax, discounts, split tender, receipt totals are fully unit-testable with `dotnet test` — no browser, no interop. `Pos.Devices` *builds commands* in pure C#; only *bytes-on-the-wire* lives in JavaScript.

### 4.2 Transport abstraction (how "prefer WebUSB" actually works)

```csharp
public interface IReceiptPrinter {
    string TransportId { get; }
    Task<PrinterCapabilities> ProbeAsync(CancellationToken ct = default);
    Task PrintAsync(ReadOnlyMemory<byte> escpos, CancellationToken ct = default);
    Task KickDrawerAsync(CancellationToken ct = default);
    event Action<DeviceStatus>? StatusChanged;
}
```

Implementations, selected by a **capability-ranked resolver**:

| Rank | Adapter | Requires | Status |
|---|---|---|---|
| 1 | `WebUsbPrinter` | Chromium + HTTPS | ✅ Implemented, tested |
| 2 | `WebSerialPrinter` | Chromium + HTTPS | ✅ Implemented, tested |
| 3 | `WebBluetoothPrinter` | Chromium + HTTPS | ⬜ Not started |
| 4 | `HttpBridgePrinter` | Local agent | ⬜ Later |
| 5 | `BrowserPrintPrinter` | Anything | ✅ Implemented, tested |
| 6 | `SimulatedPrinter` | Always | ✅ Implemented, tested |

The resolver prefers them in that order, and — importantly — a **connected fallback beats an
unpaired preferred transport**. Preferring a printer that cannot actually print would leave
the till unable to produce a receipt. Verified behaviours:

- All four available ⇒ **WebUSB** wins.
- No device APIs, print dialog present ⇒ **browser print** (the Safari/Firefox path).
- Nothing at all ⇒ **simulated**, so a terminal is never left with no printer.

**Web Serial is not merely a lesser WebUSB.** On Windows a printer is frequently held by the
system `usbprint` driver, which prevents the USB interface from being claimed while leaving
the serial port free. It is often the transport that actually works on real hardware.

**Browser printing is an honest degradation, not a silent one.** It cannot cut paper or pulse
a cash drawer, and the capability report says so — a cashier who believes the drawer opened
will walk away from an open till. It also **refuses** raw ESC/POS bytes rather than emitting a
page of control characters.

### Kitchen tickets

`RenderKitchenTicket` is a different document, not a receipt with prices hidden. Someone
assembling an order under pressure needs different things from a customer checking a total:
quantities are enlarged, prices and tax are omitted, the time is prominent, and the cut is
**partial** so the ticket stays on the roll at the pass instead of falling to the floor.

It prints **only the lines its station prepares**, and counts only those. A cook counting items
against a total that included the bar's drinks would think something was missing every time.

`StationTicketRouter` decides the split, in the pure domain and with no printer involved: one
ticket per station, in the order the shop declared its stations, skipping anything with nothing to
prepare. A sale of shelf goods produces no tickets at all, so the common case costs nothing — not
even waking a printer.

Three rules, each with a test:

- **A line with no station produces no ticket.** A bottled drink is handed over at the till.
  Sending it to a kitchen printer wastes paper and, worse, trains the kitchen to ignore tickets.
- **A voided sale produces nothing at all.** A ticket for a reversed sale is a real cost: somebody
  cooks food nobody pays for.
- **An unknown station id still gets a ticket**, printed under its raw id. A product pointing at a
  station the store has not declared is a configuration gap, and dropping its line silently is the
  one outcome that loses a customer's order.

The station is carried from the catalogue row onto the cart line, the sale line, and the stored
line, so a reprint routes the way the product was configured **when it was sold**. A product moved
from the kitchen to the bar next week must not change where a reprint of last week's order goes.
That the station survives the whole chain is an integration test, because every individual step can
look correct while the kitchen receives nothing — a dropped station does not throw and does not
warn, it just produces no ticket.

### Two printers on one till

The receipt printer and the kitchen printer are separate physical devices. A transport with no
binding attaches to the first authorised device it can open, so with two roles resolving at startup
they fight over one printer — and the symptom is not a failure. It is the kitchen ticket coming out
of the receipt printer, which looks like it worked.

A transport is therefore constructed with a `PrinterBinding`:

| State | Meaning |
|---|---|
| **Bound** | Paired to one specific device, and may use only that one |
| **Unpaired** | Never paired; attaches to nothing, so it cannot re-point the other role |
| **Any** | No role separation — the single-printer behaviour |

A bound transport deliberately **does not hunt for a working device**. If the kitchen printer is
unplugged, quietly printing its tickets on the customer-facing roll is worse than not printing
them: the operator sees paper come out and has no reason to look closer.

**The kitchen role has no fallback, and that is the design.** The receipt printer degrades all the
way down to the browser's print dialog, because a sale the customer cannot prove is not a completed
sale. For the kitchen that fallback would open a print window on the till screen in the middle of a
sale, blocking the cashier from serving the next customer to produce a ticket nobody asked for. So
`TerminalPrinterProvider.GetAsync` returns a nullable printer and `RequireAsync` returns a
guaranteed one: the two roles make different promises, and each call site states which it relies on.

The binding is read **inside the transport factory**, which is already asynchronous. Reading it
while building the container would mean blocking on async work, and Blazor WebAssembly runs on a
single thread: a synchronous wait there deadlocks the runtime outright. Writing this round's first
draft, I reached for `.GetAwaiter().GetResult()` in exactly that spot — the same mistake this
project has already documented twice as fatal.

### Label printing (ZPL)

`ZplBuilder` targets Zebra-compatible label printers. ZPL is a separate command language, so
labels get their own builder rather than a mode on the ESC/POS one. Coordinates are dots at
the head resolution, and `DotsPerMillimetre` is the constant that keeps labels physically
correct — 203 dpi is **7.992** dots/mm, not a round 8, and treating it as 8 accumulates a
scale error across a label.

Text is escaped, so a product name containing `^` or `~` — `"2~3 kg"` is ordinary retail data
— cannot truncate the field or inject a command.

**For several rounds this was reachable by nothing.** The builder and both label renderers were
written and tested while no code in the app referenced them: no label printer role, no label
screen, no way for a shop to print a shelf edge. It was the same shape of gap as the kitchen
printer — a peripheral that exists in the test suite and not in the shop.

Labels are now a third device role. A Zebra label printer and a receipt printer both appear as USB
printer-class devices, so an unbound transport would let them take each other's, and the two
languages are not interchangeable in either direction: ZPL sent to a receipt printer is a page of
literal command text, and ESC/POS sent to a label printer is nothing at all. The label role has no
browser-print fallback, for the same reason the kitchen role does not — a print dialog on the till
screen blocks the cashier, and the dialog cannot carry a control language anyway.

Media geometry is validated, and the two failure cases are deliberately different:

- **Nothing stored** — nothing was ever configured, so the default 50×25mm at 203 dpi is the only
  sensible answer.
- **Stored but out of range** — somebody configured something wrong, so the value is surfaced and
  the printer refuses, rather than the till quietly printing a size nobody chose.

A mis-sized label is worse than no label, because it comes out looking like a successful print.

**A defect this round's own tests caught.** Writing the label role, I gated the "invent a printer"
fallback on `role == Kitchen` rather than `role != Receipt`. The label role therefore fell through
to the simulated transport and reported labels as printed when nothing was paired at all — the exact
silent-success failure the kitchen role had been designed to avoid, reintroduced by naming one role
instead of excluding one. It now gates on the receipt role, so a role added later gets the safe
behaviour by default.

---

## 7. Sync verification record

Run against a live host on 2026-03-25. This is the evidence that the offline-first
guarantee actually holds over the wire, not merely in unit tests.

```
1. health                 HTTP 200: Healthy
2. provision store        storeId=01a0ac06… enrollmentCode=SWSR7YSQMUS6
3. enroll device          deviceId=01a0ac06… currency=ZAR taxMode=Inclusive
4. reuse enrollment code  correctly rejected: HTTP 400
5. push sale (first)      status=0 (Accepted)  cursor=1
6. push sale (replayed)   status=1 (Duplicate) cursor=1
7. pull sales stream      changes=1  nextCursor=1  hasMore=False
8. push without token     correctly rejected: HTTP 401
```

Steps 5–7 are the ones that matter. A push whose response is lost is retried by the
terminal; the hub recognises the client-generated id, reports `Duplicate`, and the change
log gains **one** row — so no terminal ever pulls the sale twice and no store's books are
double-counted. Step 7 confirms it end to end rather than only at the record level.

## 7a. Multi-store verification record

Run against a live hub on 2026-03-25 with two stores, driven by `tools/verify-head-office.ps1`.
That script is part of the repository, so the whole loop is reproducible rather than a
transcript of something that happened once.

```
 1. health                             HTTP 200: Healthy
 2. provision with no token            refused (as it must be)
 3. provision with a wrong token       refused (as it must be)
 4. device register with no token      refused (as it must be)
 5. provision two stores               CT01=01a0ac8e… JN01=01a0ac8e…
 6. enroll a till per store            CT01 till=01a0ac8e… JN01 till=01a0ac8e…
 7. reuse a consumed code              refused (as it must be)
 8. CT01 pushes two sales              statuses=0,0 cursor=2
 9. JN01 pushes one sale               status=0 cursor=3
10. CT01 pushes a voided sale          status=0
11. CT01 pushes a refund               status=0
12. push a record with no terminal     rejected: TerminalId is required.
13. store register                     2 stores; tills=1,1
14. consolidated report                gross=845 net takings=795 tax=110.22
15.   JN01 Johannesburg                sales=1 voids=0 gross=500 refunds=0   net=500
16.   CT01 Cape Town                   sales=2 voids=1 gross=345 refunds=50  net=295
17. assertions                         gross, refunds, net takings, sale count, void count as expected
18. backwards period                   refused (as it must be)
19. publish catalogue to CT01          published=2 rejected=1 (no barcode)
20. CT01 pulls catalog                 2 change(s)
21. JN01 pulls catalog                 0 change(s) — another store's catalogue is not visible
22.   published product shape          name=Cola 500ml price=15 storeId=01a0ac8e…
23.   station survives publication     stationId=bar
24.   payload carries every field      13 fields present
25. CT01 dispatches a transfer         status=0
26. both stores see the transfer       CT01=1 JN01=1
27.   relayed intact                   to=01a0ac8e… qty=24
28. transfer to an unknown store       stored for its author, not relayed
29. device credential on head office   refused (as it must be)
30. cross-store push                   stored against CT01, not JN01 — JN01 still shows 1 sale
31. till reads the store directory     2 store(s): CT01, JN01
32.   directory carries only names     id, code, name, isActive — no tax or currency
33.   directory without a credential   refused (as it must be)
```

**Steps 25–28 are the transfer relay.** A terminal's pull returns only its own store's changes, so
without the relay the sending store's document would be invisible to the shop expecting the goods
and they would sit in transit forever. The hub writes a second change-log row for the same record,
scoped to the destination, and the existing scoping rule delivers it — no new stream, and no
widening of the pull.

Step 25 sends its payload **the way a real till writes it**: store ids as dashed GUIDs, where the hub
mints them without dashes. Before this round the script wrote the hub's own spelling, so the ids
matched by luck and a relay that did nothing for a real till passed verification. It now fails against
the string comparison, which is how the defect was confirmed rather than assumed.

**Steps 31–33 are the branch list a till may see.** A shop cannot send stock to a branch it cannot
name, and it must not be handed the head-office token to find out — so the directory is a separate,
device-authenticated endpoint returning four fields. Step 32 fails if a fifth is added, which is the
point: the register it sits beside carries the estate's tax configuration and registration numbers.

Step 21 is what proves the boundary survived: JN01 still sees **zero** of CT01's catalogue while
receiving CT01's transfer. Only `stockTransfer` records are relayed; a sale crossing a store
boundary would let one shop read another's takings.

**Steps 2–4 and 23 are the security boundary.** Before this round, `POST /api/enrollment/stores`,
`POST /api/enrollment/codes/{storeId}`, `POST /api/devices/{id}/revoke`, and `GET /api/devices`
were **completely unauthenticated**. Anyone who could reach the hub could provision a store, mint
themselves an enrolment code, enrol devices of their own, and revoke every till in the estate —
which is a total compromise of the trust model, not a missing nicety. An enrolment code is the
bootstrap for a device credential, so a terminal able to mint one could enrol an unlimited number
of its own devices and write into the estate indefinitely.

Step 24 is the other half of that boundary, and it is enforced by *not* trusting anything in the
payload: a sale pushed by CT01's till carrying JN01's `storeId` is stored against CT01, so a
terminal in a physically insecure shop cannot move money between shops' books by editing a body.

**Step 16 is the number the whole design exists to get right.** A store that recorded two sales, a
void, and a refund must show exactly that: gross 345, refunds 50, net 295, voids counted and
*valued separately*, never netted into the takings — the same rule the shop's own Z-report follows,
because a franchise total that disagreed with the shop's own report would be worse than no total.

### Two defects this run found, which the tests had not

Both were found by driving the loop rather than by reasoning about it, which is the argument for
having a script that drives it.

**A missing `terminalId` returned HTTP 500 and failed the whole batch.** `SyncIngest` validated
`EntityId` and `EntityType` but not `TerminalId`, which is the ordering key. A null reached a
dictionary lookup and threw; because a push is one transaction, that 500 failed *every* record in
the batch. One malformed record riding along with a day's trading stopped the day's trading
reaching head office, with nothing the terminal could do about it. Now rejected per record, with a
test that a bad record does not stop the good ones beside it.

**Every refund in the estate was invisible to consolidated reporting.** `SyncPayloadIndexer`
extracted a business date only for `sale` records, so refunds were stored with a null
`BusinessDate` and excluded from every report by the indexed filter. The report still looked
complete — it simply showed gross takings as though no money had ever been handed back. Refunds
carry their amount under `totalRefund`, not `total`, so the fix is a second record type in the
indexer rather than a rename.

The second one is the more instructive: it is a defect that *looks like correct output*. Nothing
errored, no test failed, and the figure would have been believed.

### Customer-facing display

A second browser window on `/display`, driven over `BroadcastChannel`. Chosen over a server
feed or a native second-screen API because it is same-origin, needs no device API, works in
every browser, and keeps working with no network — a customer display that goes blank when
the shop's internet drops looks like the till has failed.

It is strictly an **output**. It never sends anything back, so closing it, or leaving it on a
machine that has crashed, cannot affect a sale. Every publish is best-effort.

Two details that matter: the displayed lines always sum to the displayed total (a customer who
adds up the lines and gets a different answer has reasonable grounds to distrust the till), and
staleness is tracked explicitly — a display that has silently stopped updating looks identical
to one showing an empty basket.

### Device settings

`/devices` is where the transport and sync work becomes reachable by a person setting up a
till. It exists to do two things — pair a printer and enrol against head office — and
everything else on it is diagnostics for when those fail.

Failures are described in terms an operator can act on. "This browser does not support that
connection" and "you cancelled the chooser" need completely different responses from the person
at the till, so they are not collapsed into one generic message.

The device secret is generated **on the terminal** with a CSPRNG and sent only in the enrolment
request, so it exists nowhere except the device and the hash the hub stores.

### Shifts and cash accountability

A shift is the unit of cash accountability: every sale, refund, and drawer opening during it
belongs to one person, so a shortage is answerable to a name rather than to a terminal.

`ShiftCalculator` derives the cash-up from the recorded activity rather than reading an
accumulated counter. That is the property that makes a shift auditable — two readings of the same
shift must agree, and a discrepancy must be explainable from the underlying events.

Two decisions that determine whether the variance is worth reporting at all:

- **Non-cash tenders are excluded from expected cash.** Card money never entered the drawer.
  Including it would make every shift look over by the card total, and a number that is always
  wrong in the same direction is a number everyone learns to ignore.
- **Drawer events are append-only, including no-sale openings.** Every no-sale opening is the
  classic cover for a small theft, so they are counted and never reset.

`Shift.VarianceAgainst(expectedCash)` takes the expectation as an argument rather than reading a
stored field. An earlier version read a stored `ExpectedCash`, which could drift from the derived
value — leaving two disagreeing answers to the same question with no way to tell which was right.
The stored figure survives as `ExpectedCashAtClose`, for recovering what a supervisor actually
signed off, but the derivation is what reporting uses.

**The count is blind, and the rule lives in the type, not the screen.** `ShiftReport.From` strips
`ExpectedCash`, `ClosingCount`, and `Variance` for a mid-shift reading. Hiding the figure in the UI
would leave the *printed* X-report able to leak it; making the fields null means neither the screen
nor the printer can. A cashier who can see that the drawer should hold 1 240.50 counts to 1 240.50,
and the count stops being evidence of anything.

The X-report still carries the activity — takings, the cash/card split, refunds, voids, the no-sale
count — because that is what a mid-shift reading is for. Only the drawer expectation is withheld.

### The transfers screen

Organised around the two events rather than around the document, because the two events happen in
different shops. Raising and dispatching are the sender's; booking in is the destination's, and only
appears on the till the hub relayed the document to.

Booking in is built around the ordinary case. An operator who finds everything present leaves every
box blank and clicks once — a line left blank is taken as arrived in full, and `ReceiveAsync` is only
passed the lines that were actually counted. Only someone with a shortage types numbers, and each row
shows the difference as it is typed, so the discrepancy is visible before it is committed rather than
after. A blank box must not read as zero: that would record every line of every transfer as a total
loss.

A discrepancy is reported as a **fact, not an error**. The booking-in succeeded; the shortage is the
record the two-event design exists to produce, and framing it as a failure would imply the goods were
not booked in.

The screen also says outright that in-transit goods are in **neither** store's stock. That is the
design working, and it is also the thing most likely to alarm someone looking at a stock level and
concluding the goods have vanished.

### A page is not tested until something clicks it

The screens were written, compiled, and taken on trust. Three had been looked at in a browser once;
the rest had never been executed at all. This round closed that, in two steps, and the second step is
the one that found something.

**Rendering.** All eleven pages are now rendered in the test suite by the framework's own
`HtmlRenderer`, against the terminal's real service graph with only the two browser-backed stores
swapped for in-memory ones. No new dependency, no browser, and it runs on every build. A markup
expression over a null, a loop over a sequence nobody populated, or a component parameter that cannot
round-trip now fails in the suite rather than in a shop.

Pages are rendered **populated** wherever possible, because an empty screen exercises the empty state
and nothing else — and almost every null dereference lives inside a loop over rows. That is also the
limit of the technique, and it is worth being honest about: a render test only covers the rows it was
given.

**Clicking.** bUnit drives the till and the transfers screen through their own controls — buttons
found by label and clicked, quantities typed into the boxes, keystrokes raised the way a
keyboard-wedge scanner raises them — with the assertions made against the ledger and the stored
documents afterwards. What is verified is the behaviour a shop gets, not the shape of the markup.

That is what caught the defect. **"Save draft" claimed to save and stored nothing.**
`StockTransferService.RaiseAsync` only builds the document; the dispatch is what writes it. So the
draft lived in the page's memory, the screen reported it as raised, and leaving the screen lost it.
Every service test passed because the services were correct — the missing piece was a call the page
never made, which is invisible to anything that does not go through the page.

The fix is `SaveDraftAsync`, and the properties that make staging safe are pinned: a draft moves no
stock, is deliberately **not** queued for the hub (a synced draft would have somebody counting in a
van that was never loaded), can still be sent later, cannot be saved back over its own dispatch, and
needs the same authority as sending one.

The general rule, which applies to every screen here: rendering proves the markup is well-formed;
only driving the controls proves the screen does what it says.

Both techniques were checked against a deliberate defect rather than assumed to work. The blank-count
rule was broken on purpose — an empty box read as zero instead of "arrived in full" — and exactly one
test failed, the one written for it, before the rule was restored.

### Covering the pillars, and the second defect clicking found

The technique was then applied to every remaining pillar, because one screen had already produced a
defect on the first attempt and there was no reason to think it was the only one.

| Pillar | Driven through the controls |
| --- | --- |
| Checkout | Scan a barcode, scan it twice, two products, take payment, basket locks once money is taken |
| Refunds | Find a sale by the receipt number on the slip, refund it, refund it again, ask for more than was sold, refund without authority |
| Employees and shifts | Sign in with a PIN on the keypad, refuse a wrong PIN, open the shift with the counted float |
| Cash drawer | No-sale opening with and without a reason, cash up balanced, short, and over, a mid-shift reading |
| Catalog and inventory | Physical count records the difference, write-off keeps its reason, movements are appended, a deleted product never comes back |
| Transfers | Raise, send, book in, short count, movement of stock in the ledger |
| Reporting | The day's takings, a refund subtracted and shown separately, and the shop agreeing with the estate |

The drawer screen had the same class of fault as "Save draft", in a form that costs more than lost
work. `ShiftService.CloseShiftAsync` requires the `CloseShift` permission and the screen never asked —
so a cashier was shown the entire cash-up form, filled in the count, submitted, and was refused at
that point, having already counted the drawer.

That is worse than an ordinary refusal. The drawer count is the only figure in the system that is
asserted rather than derived, so a count that is collected and then discarded is not merely wasted
work: it is the one number the whole shift design depends on, treated as if it did not matter.

The page already gated drawer-opening on `CanOpenDrawer`, so the fix was to use the rule the file was
already following: `CanCloseShift` hides the form and says who can close instead. The service was
correct throughout, and every service-level test passed — it was invisible to anything that did not
click the button.

Two of the new tests were also confirmed to detect the defect they exist for. The historical one —
a day report that ignored refunds while the estate total subtracted them, so the same shop's takings
disagreed with themselves — was reintroduced, and exactly the two refund tests failed before it was
restored.

### Offline-first, and what the shop actually sees

Two words from the brief that had never been checked past "the plumbing exists".

**The offline half was already sound, and is now verified.** The published worker is the caching one;
its asset manifest is generated; everything needed to boot is covered by the cache patterns. The risk
here is not that it is broken today but that it breaks silently later — a new file type is added, the
pattern list does not cover it, and nothing fails until a shop's connection drops. So the check reads
the patterns **out of the service worker** rather than restating them, and asserts coverage of each
category the boot depends on. Confirmed against a deliberate regression: deleting the `.css` pattern
made six stylesheets go missing from the cache, and the check named the first one.

One stale assumption was corrected in the process. There is no `blazor.boot.json` in .NET 10 — the
loader and runtime are several fingerprinted scripts with the assembly list inside them — so the
check requires those by name instead of a file that no longer exists.

**The installable half was the template, untouched.** Installing the till put a purple .NET "@" on the
home screen under the name `Pos.Web`. The manifest had no description, no scope, no maskable icon,
and a white `background_color` that disagreed with the dark till — so launching it flashed white
before the first render. The tab icon was the .NET logo.

The mark is now generated rather than borrowed: `tools/generate-icons.ps1` draws a receipt on the
till's own surface colour, in the app's palette, with a maskable variant that survives Android's
cropping and a simplified one that still reads at 32 px. Keeping it as a script rather than committed
binaries alone means the mark can be regenerated if the palette moves.

The shell stylesheet also gained the dark background and `color-scheme: dark`. The second is not
cosmetic: the till is dark and full of number inputs and selects, and without it the platform renders
every one of them as a bright white control.

**Eight megabytes of framework CSS that nothing used.** The template ships the whole Bootstrap
distribution — twenty stylesheets including RTL and unminified variants, six scripts, twenty-two
source maps — and not one Bootstrap class appears anywhere in the markup. It was dead weight that
lost every specificity contest to `pos.css`, and it made "why does this button look like that" a live
question rather than a settled one. `sample-data/weather.json` was shipping as well.

| | Before | After |
| --- | --- | --- |
| Assets to cache | 114 | 70 |
| Deployment | 28 MB | 20 MB |
| First load (brotli) | 3.33 MB | 3.06 MB |

The first load barely moved, and saying otherwise would be dishonest: Bootstrap's minified CSS was a
small part of the compressed payload. The win is in what the app is, not in how fast it starts.

### The transports were all correct and the fallback was still dead

Ringing up a sale against a recording device bridge — rather than testing each transport on its own —
is what found the most consequential defect so far.

Over WebUSB everything arrived: the receipt, its cut, the drawer kick on a cash sale and not on a
card sale, the kitchen ticket on the kitchen printer and never on the receipt printer, shelf labels
as ZPL. With the device APIs switched off, simulating Safari and Firefox, **nothing arrived at all**.

The browser-print transport is the one that works where neither device API exists. It is three pieces
and all three were disconnected: no HTML renderer existed to produce the `htmlBody` its method takes;
`ReceiptPrinter` rendered ESC/POS bytes unconditionally and never called that method; and the
transport's `WriteAsync` throws by design, because control bytes sent to a print dialog print a page
of command characters. So the fallback was selected, threw, and surfaced as a printer fault — every
sale completing, no customer receiving a receipt, on the two browsers the fallback exists for.

This is the shape worth naming, because it is the third defect of its kind here and the most
expensive: **every part was individually correct and the seam between them was missing.** The
transports' tests could not see it, the renderer's tests could not see it, and the composition root's
tests only check that services resolve. Only driving the whole path could.

The fix is `ReceiptHtmlRenderer` plus `IHtmlDocumentTransport`, which `ReceiptPrinter` routes on: a
transport that takes HTML is not a broken ESC/POS printer, it is a different kind of output. The HTML
receipt mirrors the thermal one rather than inventing a layout — same tax presentation, same
information — because a fallback that printed a visibly different document would just be a second
document to keep correct. It escapes every value, since product names and notes reach it from head
office and from an operator's typing and the result is rendered by a browser.

**A second silent seam surfaced immediately.** The print frame in `device-bridge.js` writes its own
stylesheet into the iframe, with its own class names. The renderer's first version used a different
vocabulary entirely, so a Safari user's receipt would have printed unstyled: readable, but with every
price jammed against the item name. The two files now name each other, and a test reads the stylesheet
**out of the JavaScript** and fails if the renderer emits a class it does not define — confirmed by
renaming one, which made it name `s` and nothing else.

The general lesson, which now has three instances behind it: rendering a screen and testing a service
both stop at the boundaries. What only a whole path can find is a boundary that was never built.

### The customer display, and the fourth unbuilt seam

The last named peripheral, and the same shape again.

The display is a second window fed over a same-origin `BroadcastChannel` — chosen because it needs no
device API and no server, so it keeps working with no network. A customer display that goes blank when
a shop's internet drops looks like the till has failed.

The receiving side runs a staleness timer, so that a display which has silently stopped receiving
looks different from one honestly showing an empty basket. After ten seconds without a message it
says **"Waiting for the till…"**, and it recognises a `heartbeat` message for exactly that purpose.

**Nothing sent one.** The function existed in the channel module, the display handled it, and no
caller was ever written. So a till working perfectly — a customer browsing, an operator bagging —
told the customer it had gone, ten seconds at a time, all day. The liveness indicator was wrong in
the ordinary case rather than the exceptional one, which is the failure mode that makes an indicator
worthless: everybody learns to ignore it.

The till now beats every four seconds, started by the first thing it says to the display and stopped
when the checkout screen is torn down. Stopping is not decoration — a heartbeat that outlived the till
would keep telling a display that a closed till was still running.

Two smaller things came out of the same examination:

- **The channel module had never been executed.** Not by a test, not by the one browser session the
  app ever had. It is now run under Node, which is sufficient: it touches no DOM and reaches for
  `BroadcastChannel` only inside its functions. A stub channel records what was posted, so the script
  covers what the till sends, what the display recognises, and — the case that matters most — a
  browser with no channel at all, where every entry point must answer rather than throw.
- **The support probe had no caller either.** `IsSupportedAsync` existed from the beginning and
  nothing asked it, so an operator had no way to learn that their browser cannot run a second screen.
  Device settings reports it now.

That is four defects of one shape: a correct part, a correct part, and no wire between them. Each was
invisible to unit tests, to rendered screens, and to the composition root — which checks that services
resolve, not that anything calls them. The only thing that has ever found one is driving the whole
path and looking at what came out the far end.

### The data layer, executed at last

Four defects in a row came from driving a path and looking at the far end. The largest path nobody had
driven was the one underneath all of them: `local-store.js`, the production store, 43 KB and 48
exported functions, holding every sale, refund, shift, drawer event, and transfer a terminal records.

It had never been executed. The C# side reaches it through `JsLocalStore`, but every test substitutes
`InMemoryLocalStore` — so the JavaScript was verified by reading it, and by a hand-written mirror
agreeing with a contract somebody wrote by hand. The worst data-integrity bug this project has had,
two sequence counters handing a sale and a drawer event the same position, lived in that file and was
found by reasoning rather than by running.

Running it needed an IndexedDB. A package would be the obvious route, but this repository has no
`package.json` and every verification script here runs on Node built-ins alone; keeping that property
is worth more than the last few percent of fidelity. So `tools/fake-indexeddb.mjs` models the parts the
store actually uses, and the parts that matter most are the two that make the store's guarantees
testable at all:

- **Writes are buffered and applied only on commit**, so a transaction that aborts leaves nothing
  behind — which is the guarantee `commitSale` is built on.
- **A transaction stays alive across `await`s inside it.** This is subtler than it sounds and it hung
  on the first run: real IndexedDB keeps a transaction open while the caller awaits a request and then
  issues another, and `commitSale` does exactly that several times over. Committing on a microtask
  fired between those awaits, and the promise the store was waiting on never settled.

That second one is worth dwelling on, because the shim had the same bug the real store would have had.
Modelling the guarantee is what makes the test meaningful; a shim that commits early would have
reported success for code that loses data in a browser.

Two defects fell out of the first successful run.

**Scans and searches were not scoped to a store.** The local catalogue holds one row per store per
product, and a chain sells the same barcode in every branch. `findProductByBarcode` used
`index.get(barcode)`, and the barcode index is not unique across stores, so it returned whichever row
came first: a till could scan a tin and be handed another shop's row, with that shop's price and tax,
and charge it. The name search was worse — it filtered by neither store nor deletion, so a product
head office had deleted stayed sellable through the one path that exists for labels which will not
scan.

This was an interface-level omission rather than a slip. `FindProductByBarcodeAsync(barcode)` and
`SearchProductsByNameAsync(term)` had no store parameter at all, which is precisely why the in-memory
mirror shared the gap and never caught it: both implementations were wrong in the same way, and the
tests checked that they agreed. The barcode lookup now asks for every row with that barcode and picks
the one belonging to the scanning store — `getAll` rather than `get`, because the uniqueness
assumption underneath the old call was simply false.

**Every record ever committed stayed in IndexedDB.** The `payloads` store holds a full serialised copy
of each record awaiting sync. `acknowledgeOutbox` deleted the outbox entry and left the payload, and
nothing else ever deleted one, so the largest store in the database grew for the life of the till. A
shop that fills its storage quota loses its trading — the single outcome the local store exists to
prevent. Payloads are now released in the same transaction as the acknowledgement.

The general lesson sharpens with each of these. A mirror is not a test of the thing it mirrors: two
implementations agreeing proves only that one was copied from the other, and identical omissions are
invisible to both. Where the real implementation can be executed, it should be.

### The device bridge, and a constraint finally executed instead of described

The same mirror problem applied to `device-bridge.js`. Its C# tests run against `FakeDeviceBridge`,
which was written to agree with the real module, so a mistake shared by both was invisible from either
side. What lives in that file is the most intricate logic in the system: the deferred-promise
handshake that exists because `navigator.usb.requestDevice()` only works while a user gesture is
still live. An `await` before it and the browser rejects the request, so the bridge calls it
synchronously, parks the promise against a request id, and C# polls for the outcome.

That constraint has been in the design notes since the first round and had never been run. It is now
the first thing the verification asserts, and it is asserted the only way that means anything:
`requestDevice` is called *before* `beginWebUsbRequest` returns, counted rather than trusted. The
tidy-looking regression — wrapping the call so it happens a microtask later — fails that check and
nothing else.

The rest of the surface is covered too: the printer-class chooser filter, a dismissed chooser being a
cancellation rather than an error (an operator closing a dialog must not be told the printer failed),
silent reattachment reusing one handle so the same device is not opened twice, interface selection
skipping a vendor interface to find the one with a bulk OUT endpoint, a claim refused by a system
driver reported with advice to try serial, chunked writes reassembling byte-for-byte, and closing an
already-unplugged device staying quiet.

**It found no defect.** That is the honest result and it is worth more than a contrived one: the
bridge was written carefully and reviewed in the design notes, and the value here is that its
correctness is now demonstrated rather than asserted. Not every round finds a bug, and a round that
reports one it did not find would be worse than a round that reports none.

One loop closed as a side effect. The browser print fallback — the only route to paper in Safari and
Firefox — now has both halves executed: the C# renderer's class vocabulary is checked against the
stylesheet, and the stylesheet is checked as it is actually written into the print frame, paper size
included. That agreement had broken silently once already, and a static reading of two files is a
weaker guarantee than running the one that writes the other.

A third shim came out of this, so the three Node harnesses now share a single module loader rather
than each carrying its own copy of the same copy-to-temp trick. Small, but three copies of anything is
how they drift.

### The service worker, and the four browser modules done

One browser module was left unrun: the service worker. The offline check had been reading its cache
patterns out of the source and reasoning about the manifest — a contract check, and a good one — but
the handlers themselves, the code that decides what a browser gets when a shop's internet is down, had
never executed.

It runs now in a service-worker scope built on `node:vm`, against a synthetic manifest. It is the
fourth and last browser module to be run rather than read, and with it every line of JavaScript this
project ships has been executed by something.

The branch worth naming is in the fetch handler: a navigation is served `index.html` from the cache
**unless the requested URL is itself one of the manifest's assets**. Serving an HTML document in reply
to a request for a `.wasm` file fails in a way that looks like nothing at all. Replacing that
substitution with a plain pass-through — the obvious simplification, and the one a reader is most
likely to suggest — fails two checks and then makes the offline shell test throw, which is how it was
confirmed load-bearing rather than decorative.

**No defect again.** What the checks establish is the sequence a shop actually lives through. The
worker installs while online and caches the shell, the runtime, every assembly, the stylesheets, the
icons and the globalization data. Source maps are skipped, and so is the worker itself, because a
cached copy of a running worker is a terminal stuck on an old build forever. An older cache is cleared
on activate while another application's cache on the same origin is left alone. A navigation
afterwards costs no network round trip. Once the connection goes, the shell and every cached asset
still load, and an uncached request fails honestly rather than being answered with something
plausible — a wrong answer being worse here than no answer.

Two of the harness's own faults are worth recording, because both were the shim being *less* faithful
than a browser in a way that disguised the result.

- `cache.match('index.html')` resolves relative to the worker's script URL. A stub comparing the string
  literally missed every time and reported a worker that never serves the shell offline — a shim bug
  that reads exactly like a product bug, and would have been chased as one.
- `cache.addAll` is not bookkeeping. It fetches, and rejects the entire batch if any fetch fails, which
  is what makes a worker refuse to install when it cannot cache what it needs. A stub that merely
  recorded the requests reported a successful install with the network down: a terminal that believes
  it works offline and does not.

Fixing the second also corrected the scenario the test models. A service worker installs **while
online** and only then loses the connection, so starting the harness offline was never a real
sequence — it could not have installed anything. The order is now part of the fixture, which is what
makes the offline assertions mean what they say.

That is four browser modules executed: the local store, the device bridge, the customer display
channel, and the service worker. Two of the four turned up defects when they were first run, and two
did not. Reporting the difference honestly matters more than a tidy record: a round that invents a
finding to match the previous round's shape is worse than a round that reports none.

### The terminal sequence, and what it cost to get wrong

`(terminalId, terminalSeq)` is unique on the hub, and a push is one transaction. Two independent
in-memory counters — one in `CheckoutRecordingService`, one in `ShiftService` — therefore collided
on the ordinary first minute of every shift: sale #1 and the opening drawer event were both
position 1. Separately, a browser reload reset the counter to 1, so the first record pushed after a
reload collided with one already stored, failing the **entire batch** and stranding a legitimate
sale in the outbox until it was parked as dead.

Fixed on both sides. The terminal keeps **one persisted counter** in IndexedDB and reserves a block
per commit, sized exactly so the stream stays gap-free. The hub **absorbs a reset** rather than
refusing it, assigning the next free position, while still logging a forward jump as a gap — that
being the one thing the field is genuinely for. Reassigning rather than ignoring is also what stops
a restart from looking like a gap, which would raise a false alarm on every refresh.

The sequence number is ordering metadata; the entity id is the identity. Losing a real sale to
protect a position marker is the wrong trade.

Getting there also required **extracting ingest out of the endpoint delegate** into `SyncIngest`.
The tests had been asserting a hand-written copy of the rules — a test of the copy, which would have
stayed green while the endpoint diverged. `SyncSequenceLedger` carries the per-batch state, because
a record added to the change tracker is not yet visible to a query: without it, two records in one
batch that both needed reassigning got the same position, reintroducing the collision the logic
exists to absorb.

A third defect fell out of the same work. `InMemoryShiftStore` claimed to mirror the browser
implementation "so the shift lifecycle can be exercised end to end on a build agent with no
browser", but its sales came only from a test-only `AttachSale` helper. A sale committed through the
real checkout was invisible to the cash-up — so the shift lifecycle could pass every test while the
cash-up was wrong in the only place it matters.

---

## 7b. Accessibility, and four defects only the rendered DOM could show

Every previous round verified behaviour: what was recorded, what was printed, what reached the hub.
None of them asked whether the screen could be *used* by somebody who is not reading it with their
eyes at thirty centimetres in good light — which, for a till operated for eight hours at arm's length
by somebody also handling goods and talking to a customer, is not a niche case.

So `AccessibilityTests.cs` renders each of the ten screens through the same service graph the app
uses, and inspects the DOM the browser would get. Structural checks only: a name on every control, a
name on every button, named header cells in every table, an input mode on the fields a touch till
depends on, no hijacked tab order, described images, one ordered set of headings per screen, and a
live region on every screen that reports status.

**The first run failed fifteen of eighty.** Four kinds, and none of them was noise:

1. **The status line was never announced, on all eight operator screens.** Every page wrapped its
   message in `@if (_message is not null)`, which creates the live region at the same moment as its
   content — and a screen reader announces a live region when its contents *change*. The till's entire
   feedback model, every "Added Cola 500ml" and "Sale recorded" and "A reason is required", was the
   silent first message of each page. This is the class of defect that reading the source cannot find,
   because the source looks right: the region is there, the attribute is there, the text is there. The
   bug is the *order* in which they arrive.
2. **Three controls had no accessible name**, all of them text inputs with `id=''` — the scan box,
   the catalogue search, the receipt lookup. An `<input>` with no label is announced as "edit text"
   and nothing else.
3. **Checkout had no headings at all**, and the other screens began at `h2`, announcing themselves as
   a fragment of some other page. Fixed with a visually hidden `h1` per screen rather than by
   restyling the status bar, whose layout is deliberate and already names the store, terminal and
   operator.
4. **The shift tables had no header cells.** `<td>Sales</td><td>412.50</td>` reads out as an
   unlabelled figure. They are label/value pairs, so the label is a `<th scope="row">` — with the
   stylesheet resetting weight and alignment, since a bare `<th>` renders bold and centred, which is
   wrong for a label column and would have looked like a regression to anyone comparing screenshots.

Then the first finding was turned into a rule — *a live region must be empty on first render* — and
that found two more, neither of which had been on the list:

- The transfer screen's supervisor notice was a `role="status"` region that is always full from the
  first render, so it could never have been announced. It is a condition of the page, not an event; it
  is plain text now.
- The PIN pad's live region announced nothing at all. The four dots say how far along the PIN is
  through CSS classes on empty elements, and an empty element has nothing to announce — a live region
  that looks like feedback in the source and is silence in the browser. It now carries a visually
  hidden word count, and deliberately the *count* rather than the digits: reading the PIN aloud would
  put it in the room and in the accessibility tree, which is the one thing the masked dots exist to
  prevent.

Two decisions were made deliberately rather than by omission, and both are written into the test file
where they can be argued with: `role="status"` with `aria-live="polite"` rather than `alert`, because
an assertive interruption on every scanned item talks over an operator who is mid-task with a customer
in front of them; and no live region on the 404 screen (nothing ever changes) or the customer display
(a mirror of the till, read by the customer rather than the operator, every change to which was
already announced on the till — a region there would double-announce the basket to anyone running a
screen reader on the machine driving the till).

### The fix introduced a defect the accessibility checks could not catch

`<StatusMessage Message="_message" IsError="_messageIsError" />` compiles, runs, and renders the
literal text `_message`, because a component parameter written without `@` is a *string literal* when
the parameter is a `string` — while the `bool` in the same tag was parsed as C# and styled the error
correctly, which is exactly what made it convincing. The accessibility checks passed: they assert
structure, and the structure was perfect. Seven existing behavioural tests, which assert that a
message *appears* through the controls, failed within a minute of the first run. That pairing is the
argument for keeping both kinds of test rather than replacing one with the other.

The completeness guard matters for the same reason the ingest logic was extracted two rounds ago: a
hand-written list of screens is a list that falls behind. The suite now reflects over the assembly for
routed components and fails if one is not in the accessibility list, so a new page has to answer the
question rather than inherit the wrong answer by accident.

**What it does not prove**, stated because a structural check is easy to overclaim: contrast ratios,
focus order and focus visibility, reading order as a screen reader linearises it, target sizes, and
behaviour under browser zoom or with a screen reader actually running. Those need a browser and an
assistive technology. And it says nothing about the receipt as *paper*: the print path is verified
byte-for-byte and the HTML fallback is verified, but reading an 80 mm slip aloud is a different
problem.

---

## 7c. The composition root, and why it stopped being untested

The registrations lived in `Program.cs` as top-level statements for most of this build. Top-level
statements cannot be called from a test, so the only thing that ever resolved the container was a
browser loading the page — and in this project, for several rounds, that was a screen nobody opened.

That is the worst kind of untested code. A missing registration, a wrong keyed lookup, or a
singleton over a scoped dependency all fail at **first resolution**, and first resolution in a shop
is the first sale of the morning. Meanwhile the container grew every round: two stores, three
printer roles, two payment services, four recording services, sync, and a keyed resolver per role.

It now lives in `TerminalServices.AddPosTerminal`, which `Program.cs` calls in one line and a test
can call too. The test builds the same container with `ValidateOnBuild` and `ValidateScopes`, then
**resolves every registered service** — validation alone proves the graph is satisfiable, not that
the factories run.

The sharpest assertion is that the kitchen and label printers are wired to their own role. Both are
thin wrappers that resolve happily whichever provider they are handed, so a copy-paste key passes
every other test in the suite and sends ZPL to the pass. `Role` was added to both services purely so
that is assertable rather than assumed.

Verified by breaking the container on purpose: dropping the payment registration fails 3 tests,
pointing the kitchen printer at the label role fails 1, and making `SaleCompletionService` a
singleton over a scoped recorder fails 2.

**What it does not prove:** that any screen renders. This is a service-graph check, not a substitute
for opening the app.

---

## 8. Defects found by actually running the UI

Three problems survived a clean build, 213 passing tests, and green HTTP checks. All three
were only visible in a real browser, which is the argument for running one.

### 8.1 Ambiguous route — the app rendered nothing

`Home.razor` from the Blazor template also claimed `@page "/"`, colliding with the checkout
screen. The router threw before rendering, so the page showed only the error bar:

```
InvalidOperationException: The following routes are ambiguous:
'' in 'Pos.Web.Pages.Checkout'
'' in 'Pos.Web.Pages.Home'
```

Fixed by deleting the template's sample pages (`Home`, `Counter`, `Weather`) and their now
dangling navigation menu.

### 8.2 A synchronous wait that could only ever deadlock

The printer was resolved in a DI factory with `.AsTask().GetAwaiter().GetResult()`.
Blazor WebAssembly runs on a **single thread**, so blocking on an async operation
deadlocks the runtime — there is no second thread to complete the task:

```
PlatformNotSupportedException: Cannot wait on monitors on this runtime.
  at Program.<>c.<<Main>$>b__0_3(IServiceProvider sp) in Program.cs:line 63
```

Fixed by introducing `TerminalPrinterProvider`, which resolves the printer from the
component's async startup path and caches it. **No synchronous wait on async work may ever
appear in the WASM client** — this is a hard constraint of the platform, not a style
preference.

### 8.3 Scanner focus was never actually acquired

`FocusScannerAsync` was called during initialisation, before the input existed in the DOM,
so the call was silently dropped and `document.activeElement` was empty. A keyboard-wedge
scanner types into whatever is focused, so the first scan of the day would have gone
nowhere. Fixed by moving focus to `OnAfterRenderAsync`, once the element exists.

Related: the template's `FocusOnNavigate` was removed from `App.razor`. It focuses the page
heading after navigation, which would have pulled focus straight back out of the scan box.

---

## 9. Risks & honest limitations

### 4.6 Payments

`IPaymentProvider` with `RecordOnlyPaymentProvider` now (cash, manual card, gift, split tender — **no card data stored**, keeping you out of PCI scope) and a terminal-backed provider reserved for later. Checkout never learns which one it is talking to.

**This paragraph described something that did not exist for several rounds.** The interface was named here and in the phase table, the phase was marked complete, and there was no `IPaymentProvider` anywhere in the codebase — nor any way for a cashier to take two payments for one sale. It exists now, and the claim is finally true.

The rule that makes it a real seam rather than a decorative one is that **a sale must not complete on a declined payment**. `SaleCompletionService` secures every payment and only then writes the sale. A decline records nothing at all, not even the payments already authorised, because a partially authorised sale leaves the shop holding money for a sale that does not exist.

`RecordOnlyPaymentProvider` approves everything, because it has nothing to check — the operator ran the card on a standalone machine and told the POS it went through. That is not papered over: `CanDecline` is `false` and says so, so the interface never implies a verification that never happened.

Cash is never sent to a provider. A cashier cannot decline a note they are already holding, and a till that could report a cash sale as declined would be reporting something impossible.

`TenderSession` holds the split-tender arithmetic in the pure domain. The sharp edge is that cash and everything else overtender differently: a customer handing over 100 for a 70 bill is ordinary and produces change, while "paying" 100 by card for a 70 bill is a cash advance the shop never earned. So cash may exceed the balance and every other method is capped at what is still owed.

Two smaller decisions that fell out of it:

- **The basket locks once money is taken.** Editing items against a part-paid basket would re-price something the customer has already paid towards, without telling them.
- **The till no longer projects a stored product by hand.** Its hand-written version copied the price, name, tax, and barcode and silently skipped the station, so a scanned burger produced no kitchen ticket and nothing failed — the kitchen simply never heard about the order. One shared `StoredProduct.ToDomain` means a field added to the stored row is carried everywhere.

---

## 5. Proposed solution layout

```
POS/
├─ Pos.sln
├─ Directory.Build.props                 # net10.0, nullable, analyzers, TreatWarningsAsErrors
├─ NuGet.config                          # pinned sources for reproducible restore
├─ src/
│  ├─ Pos.Core/                          # PURE domain — no browser refs
│  │  ├─ Domain/        (Product, Variant, Store, Employee, Sale, Tender, Drawer)
│  │  ├─ Pricing/       (PriceResolver, DiscountEngine, TaxEngine, Rounding)
│  │  ├─ Carts/         (Cart, CartLine, modifiers, split tender)
│  │  └─ Abstractions/  (ISyncSource, IPaymentProvider, repositories)
│  ├─ Pos.Devices/
│  │  ├─ EscPos/        (CommandBuilder, RasterEncoder, QrEncoder, Code128)
│  │  ├─ Zpl/           (ZplBuilder)
│  │  ├─ Transports/    (WebUsb, WebSerial, WebBluetooth, HttpBridge, BrowserPrint, Simulated)
│  │  └─ Resolver/      (capability ranking + honest degradation)
│  ├─ Pos.Infrastructure/  (IndexedDb stores, sale journal, sync seam)
│  └─ Pos.Web/                          # Blazor WASM PWA
│     ├─ wwwroot/js/device-bridge.js    # WebUSB/Serial/HID/BT shim
│     ├─ wwwroot/js/storage-bridge.js   # IndexedDB shim
│     ├─ Components/  (Register, Cart, Tender, Receipt, CustomerDisplay)
│     └─ service-worker.published.js
└─ tests/
   ├─ Pos.Core.Tests/          # pricing/tax/tender math
   ├─ Pos.Devices.Tests/       # ESC/POS + ZPL golden-byte tests
   └─ Pos.Web.Tests/           # bUnit component tests
```

---

## 6. Delivery phases

Each phase ends at a **demoable, verifiable** state. I will not claim a phase works without running it.

| # | Phase | Deliverable | Verification |
|---|---|---|---|
| **0** | Skeleton & gates | Solution, projects, DI, CI-style build/test scripts | `dotnet build` + `dotnet test` green |
| **1** | **Working checkout** | Scan/search → cart → tender → ESC/POS receipt → drawer kick | ✅ Complete: golden-byte tests, sync verified over HTTP, UI verified in a browser |
| **2** | Catalog & inventory | Products, variants, barcodes, stock levels, adjustments, purchase orders | ✅ Complete: 554 tests green, catalogue screen compile-verified |
| **3** | Payments & tender | Split tender, refunds/exchanges, `IPaymentProvider` seam | ✅ Complete: tender math tests incl. edge cases; refund authority enforced and tested |
| **4** | Employees & shifts | Roles/permissions, clock in/out, drawer counts, Z-report | ✅ Complete: sign-in and drawer screens, `ShiftService` over IndexedDB, blind count enforced in `ShiftReport`, cash-up slip |
| **5** | Multi-store & reports | Store scoping end-to-end, price lists, transfers, X/Z & sales reports | 🚧 Store register, head-office auth, consolidated reporting, per-store catalogue publishing, and till-side period reporting all done and verified. Stock transfers between stores and a head-office console remain |
| **6** | Sync service | Hosted sync endpoint, conflict resolution, consolidated reporting | 🚧 Ingest, pull, and consolidated reporting verified over HTTP, including a terminal sequence reset. Refresh-token rotation and change-log trimming remain |
| **7** | Hardening | AOT/trimming, bundle size, perf, accessibility, install UX | ⬜ Lighthouse + manual pass |

**Phase 1 is the milestone that matters** — a POS you can genuinely ring up sales on.

### Delivery states, honestly

| Phase | What is actually done |
|---|---|
| 1–4 | Complete, tested, and (for the till, refunds, and reports) render-checked in a browser |
| 5 | Store register, head-office authentication, consolidated estate reporting, and per-store catalogue publishing — all verified against a live hub. Multi-day periods on the till, stock transfers between stores, and a head-office console remain |
| 6 | Ingest, pull, and consolidated reporting verified over HTTP, including a terminal sequence reset. Refresh-token rotation and change-log trimming remain |
| 7 | Not started |

---

## 6. Sync architecture (decided)

You chose to build the sync server in from the start, so these are settled decisions
rather than open questions. They are recorded here because they constrain the data layer
that the checkout UI will be built on.

### Topology: hub-and-spoke

A central hub with autonomous store spokes, each holding a durable local replica that
keeps selling during a network outage and drains an outbox on reconnect. This is the
industry-consensus shape for retail POS.

**Why not mesh:** stores never need low-latency peer access, so a mesh buys nothing while
costing N² credentials and N² schema-compatibility matrices — and it destroys the
single-writer ownership that franchise pricing and royalty reporting depend on. A
store-to-store transfer is a **hub-mediated document**: store A posts a transfer-out, the
hub validates, store B pulls a transfer-in. That yields two ordinary append-only streams.

**Silver lining:** hosting the PWA on the server's own HTTPS origin satisfies WebUSB's
secure-context requirement outright, including for LAN terminals.

### Conflicts are removed by ownership, not resolved by merging

**Ownership removes the conflict, but it makes an incomplete payload destructive.** A catalogue
change is applied by *replacing* the terminal's stored row, so every field the payload does not
carry is a field the sync silently erases — no error, no warning, and the till keeps selling. What
was lost only shows up later, as work that never reached the kitchen.

That happened. The preparation station was added to the stored product for the kitchen printer, and
`CatalogChangeApplier` — listing fields by hand, written before stations existed — did not know
about it. The next catalogue sync would have stripped the station from every product in the shop.

Three guards now, because the first two are each insufficient alone:

- **One payload type, shared by both ends.** `CatalogProductPayload` is what the hub publishes and
  what the terminal applies. Two hand-written lists of the same fields is two lists that drift.
- **A round-trip test**, which builds a product with every field set and asserts it survives.
- **A reflection guard**, which compares the two shapes and fails the moment a field is declared
  with nowhere to travel. The round trip alone is insufficient: add a field, leave the test alone,
  and it passes, because both sides sit at their default.

Both were verified by reintroducing the defect and confirming they fail — the round trip reports
`StationId = kitchen` becoming `StationId = `, and the reflection guard names the field.

| Entity | Owner | Technique |
|---|---|---|
| Sales, lines, tenders | Terminal | Append-only, immutable, deduped by client UUID |
| Stock movements | Causing store | Append-only ledger; level is **derived** |
| Catalog, prices, tax | Head office | Single-writer; terminal is a read-only replica |
| Store price override | That store | Separate entity, never a mutation of the HO row |
| Employees, shifts | Clock events by store; profile by HO | Append-only events |

**Stock levels are never synced as a value.** Last-write-wins on a level silently loses a
decrement when two terminals sell the last unit, erases the audit trail needed for
shrinkage, and cannot distinguish a correction from a lost update. Instead a movement is
`{movement_id, store_id, terminal_id, terminal_seq, product_id, qty_delta, reason, ref}`
and the level is `SUM(qty_delta)` held only as a rebuildable cache. This is a G-Counter
CRDT with per-terminal replica IDs — convergence without a CRDT library.

### Transfers are two documents, not one

The plan above says a store-to-store transfer is a hub-mediated document: store A posts a
transfer-out, the hub validates, store B pulls a transfer-in. Building it made clear that it is
really **two events on one document**, and that collapsing them would be wrong.

Dispatching moves stock out of the sending store. Receiving moves it into the destination. They are
performed by different people at different tills, possibly days apart and possibly with no network
between them. One event would mean either the goods leave before anyone has packed them, or they
arrive before anyone has counted them.

Movement is recorded at **dispatch**, not at creation — a transfer being prepared has not moved
anything, and taking stock off the shelf because somebody opened a form is how a shop comes to sell
something it still has. While the transfer is in transit the goods are in *neither* store's sellable
stock, which is what stops both shops selling the same case.

**The discrepancy is the point.** The transfer records what was sent and, separately, what was
counted in. A transfer recording only what was sent shows stock leaving and never shows it failing
to arrive; one recording only what was counted loses what the sending store put on the van. Booking
in what was *sent* rather than what was *counted* is how a shortage in transit becomes invisible
stock that the destination never finds. Receiving more than was sent is refused rather than treated
as a surplus, because it is a data error and allowing it would create stock out of nothing.

Two rules carry most of the weight, and both are the same shape as rules elsewhere in this build:
**receiving twice is refused** (as closing a shift twice is), and **only the destination can book
goods in** — the sending store confirming its own dispatch would leave nobody accountable for the
gap.

Layering note: the domain returns a `StockTransferMovement` of its own rather than the ledger's
`StockMovement`, because sequence numbers come from a counter the terminal persists and identity is
the ledger's business. The first draft had the domain minting movement ids and taking a
`firstTerminalSeq`, which was both a layering smell and unbuildable — `Pos.Core` cannot see
`Pos.Infrastructure`.

### The relay: crossing a store boundary without opening one

The two ends meet at the hub, and the mechanism is deliberately the smallest one available.

A terminal's pull is store-scoped — its own changes plus global ones — which is a security property
rather than an implementation detail. Rather than widen it for transfers, the hub writes a **second
change-log row for the same record**, scoped to the destination store, and the existing rule
delivers it. Both ends see the document; neither sees anything else. Verified over HTTP: JN01
receives CT01's transfer while still seeing zero of CT01's catalogue.

Three decisions inside that:

- **Relayed records keep their entity id and type**, so the receiving terminal applies the document
  as the transfer it is rather than as a second one for the same goods.
- **An unaddressable transfer is stored, not refused.** Refusing would strand the record on the
  sending terminal while the stock had already left that shop's books, which is worse than a
  transfer needing manual attention.
- **The receiving store stores the document without queueing it and without moving stock.** The
  goods are on a van; pushing the document back would have every store accumulating copies of one
  transfer.

Applied by a router that hands each record type to the applier that knows it, so the catalogue's
rules and the transfer's rules stay in separate classes rather than in one growing switch.

One race the applier has to survive: a store dispatches, the destination receives, and the hub
relays the *dispatch* afterwards — a retry, or a queue that drained late. The local copy has
advanced further, so the stale relay is ignored. Overwriting would put the transfer back into
transit after somebody had already counted the goods in, and they would be countable a second time.
Ordering is by lifecycle position rather than timestamp, because a till whose clock is a minute
behind would otherwise be able to overwrite a receipt with a dispatch.

### The store identity, and why it had to be fixed before transfers could work

Building the transfers screen was the first time transfers were exercised **from a till** rather than
from code or a script, and it produced two defects that every existing test had passed over.

**The store id was minted fresh on every page load.** `TerminalStartup.BuildStore()` called
`StoreId.New()` unconditionally and nothing persisted it — while the store id keys every local record:
sales, refunds, employees, shifts, stock movements, catalogue, transfers, and the sale-number
sequence. A single refresh therefore hid the day's sales from every report, emptied the catalogue and
blocked it from re-seeding, orphaned the open shift, and restarted receipt numbering at one.

The terminal id *is* persisted, and the code is explicit about why; the roster also re-seeds every
load, which looks like working sign-in. The store id was simply missed. It is now persisted the same
way, and the property that matters is that it is also the **hub's** store id once enrolled — which is
what makes a relayed transfer receivable, since the receiving store accepts a document only when the
operator's own store id matches the one it is addressed to.

Enrolment consequently moves the terminal onto a different store id, and anything already recorded
belongs to a store the hub does not know and is queued to be pushed. It is erased at the moment of
the change, not at the next startup, because "Sync now" is one click away and would otherwise file one
shop's takings under another's name. The running session then holds a stale id, so the reload is a
**forced** one; the consequence is stated next to the form beforehand, where it can inform the
decision, rather than in a toast afterwards.

**Store ids were compared as strings.** The hub mints `Guid.ToString("N")`; the till's `StoreId`
renders as `Guid.ToString()` — dashed. `SyncIngest` resolved the relay destination with
`s.Id == destination`, so a transfer from a real till matched no store: accepted, stored, never
relayed, goods in transit forever, nothing on the destination's screen. Verification had passed
because the script wrote the payload with the *hub's* spelling, so the ids matched by luck. The row
also has to carry the hub's spelling, since that is what the destination's pull is scoped by.

`StoreIdFormat.Same` compares as identifiers; `Canonical` fixes the row. The verification script now
sends the payload the way a till writes it and was confirmed to fail against the old comparison. The
same mistake was then found a second time — in `StoreDirectory.TransferTargets`, where the till would
have offered to transfer stock to itself — by a test written for it.

Both defects are one mistake in different clothes: treating an identifier as text. The rule is now
that store ids are compared as identifiers everywhere, and the hub — which issued them — is the side
that must be tolerant, because a client cannot guess a canonical form its own database is already
full of records contradicting.

### The branch list a till is allowed to see

A shop cannot address a transfer to a branch it cannot name, and the head-office register is behind a
token that must never live in a shop's browser. Hence `GET /api/sync/stores`: device-authenticated,
returning `id`, `code`, `name`, `isActive` and nothing else. The projection is load-bearing — the
verification script fails if a field is added — because the register it sits beside carries the
estate's tax configuration, currency and registration numbers.

It is re-read on every visit so a new branch is immediately transferable to, and it degrades to **no
known destinations** rather than throwing when the hub is unreachable. That makes raising a transfer
impossible while offline, which is the honest outcome: the alternative is a free-text store id the hub
rejects later, after the goods have been promised to somebody.

### Idempotency

The client generates the sale UUIDv7 **before** committing locally; it is the primary key
on both sides. Local write and outbox enqueue share one transaction, and the outbox entry
is deleted only after an explicit server ack. The server does
`INSERT … ON CONFLICT (id) DO NOTHING` and returns the **same ack body** for a duplicate, so
a retried push is indistinguishable from the first and a lost response becomes harmless.

Ordering is `(terminal_id, terminal_seq)` — not a store-wide counter, since several
terminals share a store. The pull cursor is an opaque, server-assigned monotonic integer
from an explicit change-log table. **Never build a cursor from `updated_at`**: clock skew
and same-millisecond ties silently skip rows.

### Never retroactively reprice a completed sale

The customer paid, holds a receipt, and the tender is a fact. Repricing unbalances the
ledger and creates tax discrepancies. Sale lines store the price, tax, and catalog version
actually used; a variance report surfaces mispricing for head office to review, and any
correction is a separate append-only credit document.

### Enrollment

One-time, single-use enrollment code per store exchanged for a device credential.
Short-lived access tokens scoped server-side to `store_id` + `terminal_id` — the payload's
`store_id` is never trusted, which is what limits the blast radius if a terminal in an
insecure store is stolen. Refresh-token rotation with reuse detection. mTLS is skipped
because a browser cannot do it.

### Refresh-token rotation, and the ordering that makes it safe

That last clause was written in the first plan and stayed a promise for thirty rounds: the schema had
a `RefreshTokenHash` column, a `RevokedAt` flag, and audit actions named `refreshed` and
`revoked-token-reuse`, and **no code did anything with any of them**. The enrolment endpoint hashed a
random GUID, discarded it, and commented that it was a placeholder. A device credential was therefore
valid forever the moment it was issued, and the secret is attached to every sync request a till makes
— a proxy log, a crash dump, or a browser profile copied off a stolen machine is enough to write into a
store's books for as long as that store exists.

The feature is small. The reason it is worth a section is the ordering, because the obvious
implementation bricks tills.

**The hub cannot re-issue what it never knew.** Only hashes are stored, so a rotation whose response is
lost leaves the hub having rotated and the till not knowing. Two designs follow. The hub could mint the
replacement and store it in the clear until collected — which puts a credential in the database, in
exactly the place the hashing exists to keep it out of. Or the **till generates the replacement**, as
it already does at enrolment, and sends it. The second was chosen, and it collapses the hard case: a
retry is not "a second rotation", it is *the identical request*, which the hub recognises by comparing
the pair against what it already stored and answers as success.

That recognition has to be precise, because the same input means two different things:

| Presented refresh token | Replacement in the request | Meaning | Answer |
|---|---|---|---|
| Current | anything | An ordinary rotation | Rotate, keep the old secret alive for the grace window |
| Superseded | **identical** to what is stored | The lost response, retried | Success, exactly as the original |
| Superseded | different | Two parties hold this credential | **Revoke the device** |
| Unknown | anything | A wrong guess | Refuse, revoke nothing |

The third row is the point of the whole exercise. Rotation is not really about expiry — a browser
cannot keep a secret from somebody who can read the browser. It is about the one thing that is
otherwise invisible: a credential that exists in two places at once. That is why the replacement is
generated by the till rather than chosen by the hub, and it is also why getting the ordering wrong is
not a failed rotation but a **self-inflicted revocation**: a till that invented a fresh pair on each
attempt would present a superseded token with different credentials and be treated as the thief.

So on the terminal, three rules, in this order:

1. **Persist the replacement before sending it.** A crash between the two leaves the till holding the
   pair it asked for. The reverse order leaves it with no safe way to retry.
2. **Keep authenticating with the current credential until the hub confirms.** The old secret is
   accepted for a grace window (14 days) precisely so this is possible, and a failed rotation is
   therefore a non-event: the shop keeps trading and the push keeps flowing.
3. **Retry the same request, never a new one.**

All three are covered by tests, including one that asserts the two request bodies are byte-identical
across a lost response — a property no amount of reading the source would confirm.

The timings are lopsided on purpose: due after 7 days, superseded secret honoured for 14. The grace
window is not a security control, it is the price of rotating over a shop's connection. Tightening it
shortens the exposure and lengthens the queue of tills that have to be enrolled by hand.

**What it does not buy.** The refresh token sits in the same `localStorage` as the secret, so it is not
a second factor and does not survive an attacker who has the till's storage. What it does cover is the
realistic leak — the secret that escaped without the storage — turning permanent access into a bounded
window and a detection. Written down because "rotation" sounds like more than that.

Two smaller consequences worth recording. The **schema**: the hub creates its table with
`EnsureCreated`, which never alters one, so a hub that predates this round would have failed on every
authenticated request with `no such column` — an error naming neither the cause nor the fix, on the
authentication path. It now checks its own columns at startup and refuses to run with the exact
`ALTER TABLE` statements printed. Deliberately not automatic: additive statements would be trivial
here, and hand-rolled migrations are the thing that stops being trivial later. And the **grace
window's clock**: `SyncAuthentication` reads the current time, so it takes `now` as a parameter, the
same way an enrolment code already did — a test cannot wait a fortnight, and a first attempt that
shifted the stored boundary to compensate was a sign the production code should not have been reading
the clock itself.

---

## 6d. The item master, and the end of the stock chain

A feature list arrived. Most of it already existed; this is what did not, and the one thing on the list
that turned out to be the interesting part.

**The reorder trigger.** Every previous round had built the pieces of the stock story — movements
appended by sales, refunds and counts; levels derived by summing them; the distinction between "never
moved" and "zero on the shelf" — and none of them had a *use*. A number an owner can act on is the
point of the ledger, and the action is reordering. So a reorder point per article per store, and a list
derived from the same ledger as everything else.

Derived, not stored, and that is the whole design: there is no "on order" flag and no running counter to
fall out of step with the movements that produced it, and no rebuild step anybody has to remember. The
list also had to be told three separate things, each of which is a question somebody would otherwise get
wrong:

- **Is the item counted at all?** A service, a carrier bag, a newspaper returning to no shelf. This is
  a decision, which is why it is a flag on the article rather than something inferred from movement
  history. Movement is still recorded either way — the flag decides what is *reported*, never what is
  written, so turning it on later reveals the history instead of starting from zero.
- **Has somebody set a reorder point?** Null means nobody has decided, and treating that as zero would
  put every new article on the list the day it sold out.
- **Is the level actually known?** A product with no movements has never been counted. A derived zero
  there is the absence of information, not an empty shelf, and reordering against it is how a shop ends
  up with a pallet of something it already had in the back.

**A latent defect found on the way.** `UpdateLocalProductAsync` took optional overrides, and its own
documentation admitted the problem: null meant both "leave the station alone" and "clear the station",
so a caller that omitted it silently stopped a product's kitchen tickets. It happens that the one
caller passed it every time, which is why nothing had broken and why nothing would have caught it. It
now takes the whole set of editable values — what the caller wants the product to be afterwards — which
is what a screen with an edit form already knows.

**CSV, and the two rules that matter.** A shop's catalogue arrives as a spreadsheet, so the format is
the lowest common denominator on purpose. Written by hand rather than through a library, because the
two rules worth being explicit about are the two that fail silently: a field containing a comma, a
quote or a line break is quoted, and a quote inside such a field is doubled. Get those wrong and a
product called `Beans, "baked"` becomes two columns, shifting every later field on that line — the
price is read from the tax column and the shop sells at the wrong price. Reading matches columns **by
header name**, so a shop can reorder or delete columns in Excel without the importer quietly reading
prices as quantities.

Re-importing matches on **barcode** rather than on an id column, because a shop's spreadsheet has no id
column and it is the barcode that identifies an article to the person maintaining the file. Without
that, an export-and-edit cycle would leave two of every article, both of which the till would sell.

**Two things this round did that are worth more than the features.** The accessibility suite caught a
new markup defect within a minute of it existing — the file input added for CSV import had no accessible
name, so it was announced as "choose file" with no idea which file — which is the first time those
checks have caught something *new* rather than something old. And the sync payload's reflection guard
already covered the three new article fields before they were written into it, because it compares the
shapes rather than a hand-written list; the round only had to satisfy a test that was put there three
rounds ago for exactly this.

**What was not built**, and is named rather than glossed: receipts as PDF or email (the browser-print
path can save a PDF through the OS dialog, but nothing generates a PDF file and nothing sends email),
and a customer master (the `CustomerId` seam is carried through storage and sync, but there is no
customer record, no screen, and no lookup at the till). Both are additive and neither changes the shape
of a sale.

---

## 7. Risks & honest limitations

| Risk | Impact | Mitigation |
|---|---|---|
| Firefox/Safari can never do WebUSB | No direct device access there | Transport fallbacks + honest capability UI (by design, not a bug) |
| **HTTPS is mandatory** for device APIs | Production needs a real cert | Deploy over HTTPS or install as PWA from an HTTPS origin; `localhost` works for dev |
| **Cannot test real hardware from here** | Real-printer bugs invisible to me | `SimulatedPrinter` + golden-byte tests; you verify on real hardware at phase 1 |
| Blazor WASM cold start | Slower first load than JS SPA | Preloading, lazy routes, trimming; AOT in phase 7 |
| Safari/WebKit storage eviction | Risk to local-only data | `navigator.storage.persist()`, export/backup, sync seam |
| Multi-store without a server | No consolidation | Explicit `ISyncSource` seam; phase 6 |
| PCI scope | Compliance exposure | Record-only tender, zero card data stored |

---

## 8. Decisions I need from you

1. **Confirm the 2.2 trade** — no-server now + sync seam later (recommended), *or* build the sync server in from the start?
2. **Which thermal printer model(s)?** (Epson TM-T20/T88, Star, generic ESC/POS clone) — determines USB interface/endpoint quirks and whether WebUSB or Web Serial is the smoother default.
3. **Which scanner?** (Honeywell, Zebra, Datalogic) — confirms keyboard-wedge as baseline vs. WebHID enhancement.
4. **Which label printer, if known?** (Zebra ZPL vs. ESC/POS label) — I will build both, but it sets the default.
5. **Target currency, locale, and tax model?** (VAT-inclusive vs. US sales tax added at checkout — materially changes `TaxEngine` and receipt totals.)
6. **Do you have real hardware to test against?** If not, I build against `SimulatedPrinter` and you validate when hardware arrives.

**Answering #1 is enough for me to start Phase 0 + Phase 1 immediately**; the rest can land as we go.
