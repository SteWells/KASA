# KASA — Known Bugs & Lessons

> **NOTE:** This file was regenerated in the session of 2026-07-09 and contains
> only that session's findings plus carried-over entries that were still live.
> **Merge with the existing KNOWN_BUGS.md — do not overwrite it.**

Status key: `OPEN` · `FIXED` · `WONTFIX` · `DESIGN` · `EXTERNAL`

---

## Module Manager syntax traps

These both fail **silently or misleadingly**. MM does not error cleanly, so
the symptom always appears somewhere other than the cause. Both cost multiple
debug rounds this session.

### CONFIG-001 — Config nodes must be MULTI-LINE `FIXED`
An inline node body is **not** parsed. MM reads the whole string as a single value.

```cfg
# WRONG — the resource name becomes
# "PrismaticGel  ratio = 1.0  DrawGauge = True"
PROPELLANT { name = PrismaticGel  ratio = 1.0  DrawGauge = True }

# RIGHT
PROPELLANT
{
    name = PrismaticGel
    ratio = 1.0
    DrawGauge = True
}
```

**Symptom:** `<garbage> not found in resource database. Propellant Setup has failed.`
followed by a *downstream* NRE, and then an unrelated-looking
`Cannot find a PartModule of typename '...'` because the engine module never
registered.

Bit us twice: `PROPELLANT` + `atmosphereCurve` in `KASA_PrismaticGelEngines.cfg`,
and `INSTRUCTOR` + `KERBAL` in `10c_TheConvergence.cfg`.

### CONFIG-002 — Variable search on INSERT must be a NODE PATH `FIXED`
When MM **inserts a new node**, `#$...$` can only resolve a *node path*.
Bare keys and parent-relative refs both throw.

```cfg
baseVolume = #$/RESOURCE[LiquidFuel]/maxAmount$   # works (node path)
baseVolume = #$../kasaTankVolume$                 # throws
baseVolume = #$/kasaTankVolume$                   # ALSO throws (bare key)
```

**Symptom:** `Error - Cannot parse variable search when inserting new key ...`,
once per matched part (89 errors in our case). The key is simply never set, so
downstream values silently become `0`.

**Consequence:** there is **no arithmetic** available at insert time. Work around
it by deriving from one node path and scaling `unitsPerVolume` on the tank type
instead (see the LFO 20/9 scaling in `KASA_FuelTanks.cfg`).

---

## Part / module naming

### CONFIG-003 — The stock multi-mode module is `MultiModeEngine` `FIXED`
Not `ModuleMultiModeEngine`. There is no such class.

**Symptom:** `Cannot find a PartModule of typename 'ModuleMultiModeEngine'`,
once per patched engine. The module never attaches → no mode toggle in the PAW →
engine stays in its stock mode → 0 dV with a tank full of Gel.

Masked for two rounds because CONFIG-001 was throwing the *same* error message
for a different reason. **Lesson: after fixing an error, re-check the log to
confirm that specific line actually cleared.**

### CONFIG-004 — Terrier's PART name is `liquidEngine3_v2` `FIXED`
The *folder and filename* are `liquidEngineLV-909_v2`; the `PART` name inside
is `liquidEngine3_v2`. Patch the PART name.

**Symptom:** MM logs the config but no `Applying update ... to ...` line, and the
patch silently no-ops. Verify by grepping the log for `Applying update KASA/...`.

---

## Contract Configurator

### CC-001 — `DATA_EXPAND` parents are not addressable contract types `FIXED`
A contract using `DATA_EXPAND` exists only as its expanded children
(`MA_Monoliths.0`, `.1`, `.2`). `CompleteContract { contractType = MA_Monoliths }`
cannot resolve and kills the whole contract type.

Affected `MA_UFO` and `MN_Monolith`. Reference the children explicitly.

### CC-002 — `animation` is a strict enum `FIXED`
An invalid instructor animation name throws `ArgumentException: Requested value
'true_excited' was not found` and **fails the entire CONTRACT_TYPE**.

Known-good set in use: `idle`, `idle_lookAround`, `idle_wonder`, `true_nodA`,
`true_nodB`, `true_smileA`, `true_thumbUp`, `true_thumbsUp`, `false_disagreeC`,
`false_disappointed`, `false_sadA`.

Invented and rejected: `true_excited`, `idle_sad`.

---

## Behaviour that looks like a bug but isn't

### NOTE-001 — Prismatic Gel engines don't move the ship on the pad `WONTFIX`
**Intended.** `atmosphereCurve` is `3500s` vacuum / `350s` sea level, so at 1 atm
thrust is scaled to ~1/10th. Fuel drains, dV falls, ship sits still.

Gel is a **vacuum-only transfer propellant** by design. Read the *vacuum* dV in
the VAB, not the atmospheric figure.

### NOTE-002 — Burn duration is independent of Isp `FIXED`
`burn time ≈ (ship mass × dV) / thrust`. Low-thrust Gel modes produced hour-long
burns while saving no time. Gel `maxThrust` now equals each engine's **stock
vacuum thrust**, so a Gel burn takes the same wall-clock time as the stock engine
but delivers ~10× the dV from the same fuel mass.

---

## B9PartSwitch

### B9-001 — `upgradeRequired` cannot hide subtypes `WONTFIX`
Hides the *entire* switcher when only one subtype is unlocked; evaluated once at
`OnStart` and never re-evaluated; part-info tooltips list all subtypes regardless
of lock state; does not reflect a runtime `SetUnlocked`.

**Not usable for discovery gating.** We use "Option A": subtype names visible,
gated naturally by resource availability (you cannot fill a tank with a resource
that has no source).

### B9-002 — PAW manipulation must be HIDE-ONLY `WONTFIX`
Never force `guiActive = true`. Setting it on incompatible fields throws
`BaseField.GetStringValue InvalidCastException` and corrupts the whole part-action
window (~8,500 exceptions once). Only ever hide.

---

## External / not ours

### EXT-001 — Drill-O-Matic (full size) missing from the parts list `OPEN`
`PartCompiler: Cannot clone model from 'Squad/Parts/Resources/RadialDrill'
directory as model does not exist` → part dropped.

**Cause:** ReStock strips the stock model (`TriBitDrill.mu`); its replacement-model
patch is then skipped because `MyModuleManagerPatches/RestockPatchDisabler` adds
`RestockIgnore` to `RadialDrill`. MiniDrill isn't in that target list, which is why
the junior drill survives.

**Fix:** remove the `@PART[RadialDrill]` block from `RestockPatchDisabler.cfg`
(or drop `RadialDrill` from its target list). **This is in Stephen's own patch
folder, not KASA.**

### EXT-002 — Parallax `RaymarchedShadowsRenderer` NRE on scene teardown `OPEN`
`NullReferenceException ... RaymarchedShadowsRenderer.DebugShadowComponent ()` in
`OnDestroy`. Cosmetic, fires on scene unload. Low priority, not KASA.

---

## Design debt

### DESIGN-003 — Option 3: dedicated Gel parts (engines + tanks) `DESIGN`
The current spoiler: every fuel tank in the game advertises a **Prismatic Gel**
subtype from the first launch, and the six curated engines show a Gel mode toggle.
Mild, but present from minute one.

**Why it's now more attractive than first assessed:**
* KASA already ships 5 custom parts with their own `.mu` + textures under
  `KASA/Parts/` (Watcher, Gazer, Seeker, Far Reach, Harbinger). The workflow
  is known.
* Assets living in `KASA/Parts/` are **immune to ReStock** (ReStock only deletes
  Squad's models — cf. EXT-001).
* A donor parts mod with a permissive **asset** licence (CC-BY, CC-BY-SA, MIT,
  Apache, CC0) removes the redistribution problem *and* the need to recolour,
  since a donor engine already looks unlike a Terrier. Check the mod's own
  `LICENSE` file; code and asset licences are often different.

**Why it hasn't been done:** ~15–20h of config work, and the payoff hinges on an
unproven assumption.

**De-risking order (do NOT reorder):**
1. **Spike the hiding.** One throwaway part using `TechHidden` / `category`,
   flipped at runtime by the plugin on resource discovery. If parts can't be
   truly hidden from R&D and the VAB, option 3 loses most of its value — and
   you've spent an afternoon, not a fortnight. NB: runtime part manipulation is
   the same class of thing that caused B9-002; test carefully.
2. **Find the donor mod**, read its actual `LICENSE`.
3. **Port one tank and one engine end-to-end** before doing the rest.

**Cheap middle ground:** restrict the Gel/DenseOxidiser/ThermicMix subtypes to a
handful of tanks instead of every LFO tank in the game. Five minutes, much less
spoiler surface, no new parts.

**Partial fix for engines only:** extend `KASAResourceGate` to *hide* the mode-toggle
PAW field until PrismaticGel is discovered (hide-only, per B9-002). Does nothing
for tanks.

### DESIGN-004 — `MultiModeEngine` supports exactly two modes `DESIGN`
`primaryEngineID` + `secondaryEngineID`. "Toggle Mode" flips between those two;
it will not cycle through three.

Therefore each fuel gets its **own curated engine set**: Gel on the current six;
DenseOxidiser and ThermicMix on *different* engines when Ike and Moho come online.
This keeps each fuel tied to the planet that produces it, which is the intended
narrative anyway.

### DESIGN-005 — Dead code in `KASA.cfg` `OPEN`
* Both `DATA` nodes are unreferenced. Nothing uses `@/probeBase`, `@/crewedBase`,
  `@/stationBase`, `@/baseBase`, `@/earlyBase`, `@/isruBase`, or the
  `CelestialBody` refs (`@/Mun`, `@/Moho`, …). Every contract computes
  `contractTotal` inline instead.
* `TheISRUProgram` (sortKey 09) is an orphan group — ISRU folded into the Bases
  Programme. `TheOuterPlanetsProgram` (sortKey 06) is declared and **reserved**
  for the unwritten outer arcs.

Both annotated in-file. Deleting them is a real (if small) functional change —
decide deliberately.

---

## Content debt

### CONTENT-001 — Placeholder coordinates in `10c_TheConvergence.cfg` `OPEN`
Every waypoint (Duna Face, Dres, Pol, and the Bop wreck) uses **placeholder
lat/lon**. Set them to the real easter-egg locations, or wherever reads best for
the narrative sites. Flagged inline and in the file header.

### CONTENT-002 — Dres and Pol have no stock monolith `OPEN`
Those two bearings are **narrative** waypoints: the player lands and the story
places a monolith, but there is no physical object to see. Real objects would need
a PQSCity / Kerbal Konstructs static — out of scope for config.

### CONTENT-003 — Reward multipliers are unbalanced placeholders `OPEN`
All deep-space contracts use flagged multipliers (`x4` deploy, `x6` outer anomaly,
`x8` inner-planet tier) over the inline `contractTotal` formula. Never balanced.

### CONTENT-004 — Outer-planet economy arcs unwritten `OPEN`
Duna+Ike, Dres, Jool system, Eeloo. Special cases to decide: Jool is a gas giant
(orbital/moon arc only), Laythe has atmosphere + oceans (an Eve-like "hard surface"
question), Tylo is airless but brutal, Eeloo is the Elysium jackpot and the
returnable payoff world.

The Convergence (10c) deliberately does **not** depend on these — the monolith
trail can be followed to Bop without running any outer resource survey.

### CONTENT-005 — Gel engine plume FX `OPEN`
Gel mode thrusts correctly but has no visible plume. Cosmetic; deferred until
thrust values settled (they now have — NOTE-002).

---

## Housekeeping

* `KASA_Shipyard.cfg` — **delete if still present.** It reintroduces EL's
  RocketParts double-charge. `KASA_ELOverride.cfg` already does the correct
  wipe-and-substitute.
* Contract filenames now match their group `sortKey`. When dropping in the
  renamed files, **delete the old ones** (`05_TheKerbinAnomalies`,
  `06_TheStationsProgram`, `07_TheBasesProgram`, `08_TheMunMinmusAnomalies`,
  `09_TheInnerPlanetsProgram`, `10_TheConvergence`) or CC will load duplicate
  `CONTRACT_TYPE` definitions.
* SCANsat caches resource overlays on existing saves. Abundance changes need a
  fresh scan to display (they take effect immediately regardless).
