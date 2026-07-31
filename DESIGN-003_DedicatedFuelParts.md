# DESIGN-003 — Dedicated fuel parts (hidden until earned)

Status: **awaiting sign-off** — nothing built yet.
Supersedes: the curated-subtype middle ground in `KASA_FuelTanks.cfg`
(that stays in place until this lands, then is retired).

---

## 1. Why this is now possible

The hiding spike (`KASA_HideSpike.cfg` + `KASAPartHideSpike.cs`) proved:

* `TechHidden = True` + `category = none` hides a part **completely** —
  invisible in the tech tree AND the VAB, including both search boxes.
* Flipping those two fields at runtime makes the part behave as normal.
* A craft already containing the part still loads and launches while the
  part is hidden. Hiding **hides, it does not disable**. Acceptable: you
  can't have such a craft before unlocking, except by importing one from
  another save, where the surprise is gone anyway.
* Toggling from inside the VAB may not refresh the list; toggling from
  another scene works. **Not a problem** — discovery happens in flight,
  and reaching the VAB is a scene change.

Because the spike used `+PART` cloning, dedicated parts need **no new
models or textures**. This is config work, not art work.

---

## 2. What this replaces

| Now | Becomes |
|---|---|
| B9PartSwitch subtypes on shared tanks | dedicated hidden tank parts |
| `MultiModeEngine` second mode on stock engines | dedicated hidden engine parts |
| `PartUpgradeManager` / `upgradeRequired` unlocking | `TechHidden`/`category` flip |
| Curated tank list (spoiler mitigation) | not needed — parts are hidden |

**Save-breaking, deliberately.** Removing subtypes/modes breaks vessels
using them. Agreed this is the right moment: the mod is still in
development, not in real use. Do it before the playthrough gets long.

This also removes the `MultiModeEngine` two-mode ceiling — the reason
Gel and Aetherium needed separate curated engine lists.

---

## 3. Unlock gates

Two **separate** gates, deliberately:

* **Resource scan** (80% coverage) → the body's **drill** becomes
  available. Existing behaviour, unchanged (`KASAResourceScanned` →
  `MarkResourceScanned`).
* **Crewed landing + sample return** → that fuel's **engines and tanks**
  become visible. New.

Rationale: you can dig it up once you know it's there, but you can't use
it as propellant until Wernher has a physical sample in hand. This is
already the narrative beat in `KSP_02_MunCrewed` ("he needs a physical
sample before he can tell us what it actually is").

| Fuel | From | Body | Gate contract |
|---|---|---|---|
| Prismatic Gel | Glassmonite | Minmus | `KSP_04_MinmusCrewed` |
| Heavy Blend | Kerium | Ike | `OP_06_Base` *(see note)* |
| Thermic Mix | Moherium | Moho | `IP_03_Crewed` |
| Aetherium | Elysium | Eeloo | `OP_16_Crewed` |

**Ike note:** Ike has no crewed sample-return contract (probe + base
only), so Heavy Blend gates on the base instead. A crewed base with an
ISRU *is* on-site analysis — no need to fly Kerium home. Agreed.

Non-fuel resources for reference (unchanged): Regocite →
`KSP_02_MunCrewed`, Ferrosite → `OP_02_Crewed`, Laythite →
`OP_11_Crewed`.

---

## 4. Engine inventory — 11 engines

### Prismatic Gel — 7 engines (mix kept as-is)
Cloned from: Spark (0.625), Terrier (1.25), Nerv (1.25), Poodle (2.5),
Wolfhound (2.5), Rhino (3.75), Dawn (0.625 ion).
Role: deep-space efficiency. Keeps the existing spread — the one place
variety is wanted.

### Heavy Blend — 2 engines: 1.25 m and 1.875 m
**CORRECTION.** An earlier revision of this doc called this a
bipropellant (LF + oxidiser), following the old lifecycle notes. That was
wrong: the implemented resource is **single-resource**, filling the whole
LFO volume, exactly like the other three KASA fuels. Kept that way for
consistency and to dissolve the mixture-ratio question entirely.

Renamed `DenseOxidiser` -> `HeavyBlend` ("Heavy Blend") because
"oxidiser" implied a pairing that does not exist. Narrative: refined
Kerium, the metallic fraction concentrated into a dense storable
propellant — real metallized-propellant research trades Isp for density
in exactly this way, which suits Kerium being a dense metallic mineral.

The trade is **density for Isp**: `unitsPerVolume` raised 2.22222 -> 3.0,
giving ~0.0156 t per tank volume against LFO's ~0.0111 (about 1.4x
denser). Since tank dry mass scales with volume, that means less tankage
per unit of propellant — valuable when hauling fuel down to a surface and
back. Isp is deliberately below stock LFO, so it is wrong for transfer
stages and right for compact high-thrust landers.

| | 1.25 m | 1.875 m |
|---|---|---|
| Thrust (vac) | ~90 kN | ~250 kN |
| Isp vac / asl | 300 / 265 | 305 / 270 |
| Gimbal | 4 deg | 4 deg |
| Mass | ~0.7 t | ~1.8 t |

Use case: Tylo and Laythe descent stages, two lander weight classes.

### Thermic Mix — 1 engine: 2.5 m
**Solar thermal.** Nominal ~180 kN, Isp 380, mass ~2.5 t. Thrust scales
with solar distance (section 6). Single size deliberately: inner-system
interplanetary is a narrow band (Moho probe, Moho crewed, Eve orbiter)
and players cluster engines for more. A 3.75 m version would tread on
Aetherium's role.

### Aetherium — 1 engine: 3.75 m
Endgame mothership drive. High Isp (~1000), modest thrust (~300 kN),
mass ~4 t. **The four existing Aetherium engines (Spark, Cub, Cheetah,
Skiff) are removed** — a Spark burning endgame mothership fuel fails the
"fuel must suit the use case" test.

*All stats above are proposals, flagged for playtest.*

---

## 5. Tank inventory — 8 tanks

Tanks must be hidden parts too, or the spoiler just moves from engines
to tanks.

| Fuel | Sizes | Contents |
|---|---|---|
| Prismatic Gel | 0.625, 1.25, 2.5, 3.75 | pure PrismaticGel |
| Heavy Blend | 1.25, 1.875 | pure HeavyBlend (`unitsPerVolume` 3.0) |
| Thermic Mix | 2.5 | pure ThermicMix |
| Aetherium | 3.75 | pure Aetherium |

Sizes match the engines that burn them. Optional later: long/short
capacity variants — deliberately omitted for now to keep the list small.

**Narrative note:** KSP `RESOURCE_DEFINITION` has no description field,
so the "refined Kerium" story has to live in the part descriptions of
these tanks and engines (and in contract text), not on the resource.

**Resolved:** no mixture ratio needed — all four fuels are
single-resource. Engine `PROPELLANT` and tank `unitsPerVolume` reference
one resource each.

---

## 6. Solar-thermal plugin (`KASASolarThermal`)

### Real-world basis
Solar thermal propulsion concentrates sunlight onto a heat exchanger to
heat propellant directly — no combustion. NASA/AFRL studied it for
decades (the "Shooting Star" flight experiment); predicted Isp ~700-900 s
with hydrogen. Performance tracks solar flux, which falls off as 1/r^2.
So it genuinely works better closer to a star.

Its hard engineering problem is surviving concentrated-solar temperatures
— which is exactly what **Moherium** is described as solving. The reward
for reaching Moho is the propellant that doesn't break down under solar
concentration.

### Mechanic
* Continuous, **not** SOI-snapped. Most of a Moho insertion burn happens
  in Kerbol's SOI, so there would be nothing sensible to snap to.
* `flux = (kerbinSemiMajorAxis / distanceToKerbol)^2`, clamped
  **0.2 to 1.75**.
* Scale **thrust**, hold Isp fixed. Physically right for a power-limited
  system (flux limits mass flow at fixed exhaust velocity) and far
  simpler than regenerating `atmosphereCurve` at runtime.

Sample points on the curve:

| Location | Multiplier |
|---|---|
| Moho / Eve | 1.75x (clamped) |
| Kerbin | 1.0x |
| Duna | ~0.43x |
| Dres / Jool / Eeloo | 0.2x (floor) |

Deliberately **bad** in the outer system — a specialist, not an upgrade.
That is what earns the Moho programme its place.

### Implementation notes
* `PartModule` on the Thermic engine only. ~80 lines.
* Distance from `FlightGlobals` sun position to vessel CoM; reference
  from the home body's `semiMajorAxis`.
* **Field choice is the real risk, not the maths.** Preference:
  `ModuleEngines.multFlow` (scales flow, keeps Isp). Fallbacks:
  `thrustPercentage` (works but stomps the player's tweakable) or
  `maxThrust` (display oddities). To be verified against the KSP API
  docs while building.
* PAW readout showing the current multiplier, so the player can see why
  thrust changed.
* VAB shows nominal 1.0x (no sun distance in the editor) — document it.
* Needs playtest: behaviour in staging, under time warp, and on
  vessel load.

---

## 7. Gating machinery

Mirrors what already works rather than inventing new patterns.

1. **Scenario flag** — `UnlockedFuels` (HashSet) in
   `KASADiscoveryScenario`, saved/loaded like `BodyResourceScanned`.
2. **CC behaviour** — `KASAFuelUnlocked { fuel = PrismaticGel }`, a
   near-copy of `KASAResourceScannedBehaviour` but firing on **contract
   completion** rather than a parameter state change.
3. **Part gate addon** — the spike's `SetVisible()` promoted to a real
   addon, driven by one central mapping file `KASA_GatedParts.cfg`:

```
KASA_PART_GATE
{
    fuel = PrismaticGel
    part = kasa_gel_terrier
    part = kasa_gel_poodle
}
```

One file to audit, rather than a flag buried in 19 part configs.
Applies on scene load and on unlock.

---

## 8. Build order

1. Gating machinery (scenario flag, behaviour, part-gate addon) — testable
   with the spike part before any real parts exist.
2. Heavy Blend (2 engines + 2 tanks) — simplest, config only.
3. Aetherium (1 engine + 1 tank), remove the 4 old Aetherium patches.
4. Prismatic Gel (7 engines + 4 tanks), remove the 7 mode patches.
5. Thermic Mix engine + tank, then the solar-thermal plugin.
6. Retire the curated tank list in `KASA_FuelTanks.cfg`.
7. Delete the spike files.

Config-only steps can be dropped straight in; steps 1 and 5 need a
`KASA.dll` rebuild.

---

## 9. Risks

* **Save-breaking by design** (section 2). Do it now, not later.
* **Solar-thermal field choice** unverified (section 6).
* **Tech tree placement** — hidden parts still need a node so they
  become purchasable when revealed. Existing fuel parts' nodes should be
  reusable; needs a check against `KASA_TechTree.cfg`.
* **Part count** goes from 0 dedicated to 19. All hidden until earned, so
  the player's parts list does not bloat.
