# KASA Logistics — Universal Control UI (design addendum)

Extends `LOGISTICS_DESIGN.md`. Status: **design agreed, build in progress.**
Everything below is signed off; this file is the record, not a proposal.

## Motivation
The PAW is overloaded (ISRU converter actions + logistics actions + processing
actions on one part), control is confusingly anchored to whichever part you
open, and a drilled-resource base with no ISRU never registered as a hub because
the marker only rode on ISRUs and labs. A single toolbar-launched window fixes
all three: one place to see every hub and route, control in either direction,
and a marker that lives where every vessel actually has one.

## Model boundary (unchanged)
`KASALogisticsScenario` stays the single source of truth. The window is a *view
+ controller* over it; no pooling/dispatch/routing logic moves. Every window
action calls an existing scenario method.

## Decisions
- **Universal reach.** The window manages *any* hub from anywhere, not just the
  one you're parked at. Scenario methods already read unloaded hubs via their
  proto-vessels, so this is a view concern. Caveat carried over: background
  production isn't simulated yet, so unattended bases stop producing — a remote
  hub's pool figures can be stale until it's loaded.
- **Marker moves to command parts.** `KASALogisticsHub` is MM-patched onto
  command pods/probe cores instead of ISRUs and labs. Any vessel can therefore
  be a hub. (Third-party modules already stack modules on command parts here —
  MechJeb, kOS, KerbalEngineer — so co-existence is a non-issue.)
- **Opt-in via Register as hub.** Because the marker now rides on every command
  part, a vessel only *acts* as a hub once the player registers it in the
  window. Stored as a `HashSet<string>` of vessel Guids in the scenario, with
  Save/Load and Guid-migration (mirrors `HaulerBusyUntil`). On first load of an
  existing save, auto-register any vessel referenced by an existing route
  (`HubA`/`HubB`) so the current network survives the update.
- **Two modules, one part.** `KASALogisticsHub` (marker, PAW stripped) and
  `KASALogisticsTug` (recording + load/unload) both sit on command parts. Not
  merged — merging renames a persisted module and risks save proto-vessels.
- **ClickThroughBlocker.** Already in the install; the window uses CTB
  (`ClickThroughFix` GUI wrappers), not a hand-rolled guard.
- **Self-exclusion guard.** A crewed hauler must never pool from itself.
  Implemented (see Phase 1).

## PAW split (what the player still sees on parts)
- **Tug (command part):** Start / Complete / Cancel Logistics Run, Load Cargo
  From Hub, Unload Cargo To Hub. These are physical "act as this vessel, here"
  actions and stay under the pilot's thumb.
- **Hub (command part):** marker only, no PAW buttons. ISRU/Lab menus return to
  stock converter/lab actions.

## Window contents
Toolbar button opens a panel listing every registered hub. Under each hub:
- its routes as **bidirectional edges**, each with **Send** (dispatch from this
  end) and **Request** (dispatch from the far end — mirror of Send, same
  `Dispatch` call, source swapped). This is the fix for the control confusion:
  both endpoints visible, click the direction you want.
- **in-transit runs** as live rows with a **countdown** read from
  `KASADispatch.ArrivalUT`.
- **standing orders per resource**, each showing live status ("running",
  "hauler in transit", "origin below reserve", "destination at fill target"),
  editable in place.
- a **Register / Unregister as hub** control for the vessel.

Resource selection is a window control (per action / per order), replacing the
per-part `selectedResource` field.

## LF/Ox routes — seam kept open (NOT built now)
Fuel vs cargo is classified in exactly one place: `IsCargo(resourceName)`.
`SampleFuel` counts non-cargo resources as burn; `TrackPeak` counts cargo
resources as payload. LF/Ox can't be cargo today because the recorder sums a
resource across the whole vessel and can't tell an engine feed tank from a
holding tank.

To add LF/Ox later (localized to that seam, no rewrite):
1. Mark holding tanks as cargo tanks (a lightweight `KASACargoTank` PartModule,
   or match the holding-tank part identity).
2. Split `VesselResources` into cargo-tank vs fuel-tank halves.
3. `TrackPeak` uses cargo-tank amounts; `SampleFuel` uses fuel-tank amounts.
4. Add LiquidFuel/Oxidizer to the cargo set.

**Forward-compat rule enforced now:** the window never hardcodes a resource
list. Selectable cargo per route is derived from that route's recorded
`Payload` plus the cargo set, so LF/Ox appears automatically once whitelisted.

## Build order (incremental, each testable, never leaves the player without
## a control surface)
- **Phase 1 — safe groundwork (DONE).** Additive, keeps all current PAW
  buttons. (a) Runs sized against per-resource `Payload` not total capacity —
  no-op for single-resource routes, correct for multi and future LF/Ox.
  (b) Self-exclusion guard: optional `exclude` vessel threaded through
  `LocalPool`/`PoolAmount`/`PoolSpace`/`PoolTake`/`PoolAdd`; Load/Unload pass
  the hauler. Existing callers unchanged via default arg.
- **Phase 2 — the window (DONE).** New `KASALogisticsUI.cs`: ApplicationLauncher
  button (CTB-wrapped window), universal hub/route list, Send/Request, live
  countdowns, per-resource orders, register toggle. Adds the `RegisteredHubs`
  registry + Save/Load/migration to the scenario. Both PAW and window live
  simultaneously through this phase.
- **Phase 3 — cleanup (DONE).** Strip
  `KASALogisticsHub` PAW events (keep as marker); move the MM patch from
  ISRUs/labs onto command parts. Only after the window has replaced the
  control surface.

## Open items
- Toolbar icon: 38×38 PNG. Placeholder until supplied.
- `AllHubVessels` gains a `RegisteredHubs` filter in Phase 2 (Phase 1 leaves it
  as-is so nothing de-registers before the window exists).


## Active routes (supersedes standing orders)
Standing orders (origin-anchored push, hidden per-hub list, reserve/fill-target)
were **removed** — the direction was confusing and orders were hard to find. They
are replaced by a per-route **Active** toggle. Round trips only; one-way routes
keep manual Send/Request.

**Direction** is captured automatically at recording: the hub you first `Load` at
is the source, the other end is the destination (`KASARoute.SourceHubId`, written
in `CompleteRecording` from `KASAActiveRecording.SourceHubId`, set in the tug's
`LoadFromHub`). A swap capability is intentionally NOT surfaced; re-record if a
legacy route has no direction. Existing pre-change routes fall back to HubA = source.

**Lifecycle** (`TickActiveRoutes`, runs every ROUTE_INTERVAL, foreground and
background): Idle → load a full load at the source (drains it so it keeps
producing) → Staged (hold loaded at source) → when the destination has room for
the load, charge fuel and fly the delivery leg → InFlight (deliver mid-way, then
return) → Idle. Staging is the key: cargo leaves the source tank on load, so the
base keeps drilling even while the destination is backed up. Failure modes surface
as status lines: "waiting for cargo" (source can't fill), "staged: destination
full" (destination can't accept), plus in-transit ETA.

**Settings per route:** `Active`, `WaitForFull` (default true; false = ship partial,
still full fuel), auto-captured direction, `LastStatus` for the window.

**Window:** each round-trip route row gets the Active toggle + live status; manual
Send/Request are disabled while a route is Active (prevents double-booking the
hauler). No orders list, no reserve/fill fields.

**Fuel** is charged at departure via the shared `ChargeRoundTripFuel` helper
(extracted from `Dispatch`), hauler tanks first then the source pool.

**Deferred:** multi-resource routes work through this path (one dispatch per staged
resource) but recording a genuine multi-resource manifest is still unproven.
LF/Ox routes remain the open nice-to-have (part-aware fuel/cargo seam).
