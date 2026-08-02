# DESIGN-006 — Recon / Survey split, and visual-scan reveal

Status: **agreed, not yet built.**
Supersedes: the single-probe-contract pattern in files 04, 05, 06.

---

## 1. The bug that started this

`KSP_01_MunProbe` carried three scans (altimetry, biome, resource) and
advanced the body to **stage 5 on altimetry** and **stage 6 on biome**.

SCANsat's biome scanner (multispectral, wide swath) reaches 80% coverage
*before* the narrow-swath altimetry scanner. So biome fired first, taking
the body straight to stage 6. When altimetry later completed, stage 5 was
a no-op, because `AdvanceBodyToStage` never regresses.

Result: the Mun jumped from blurred (stage 4) to fully revealed (stage 6),
skipping the charted intermediate entirely.

**Root cause is ordering, not the ladder.** Two scans in one contract, with
no guarantee which finishes first.

---

## 2. The principle

KASA is a mod about **controlling what the player sees**. Discovery stages
drive appearance only:

* `SetBodyVisualLevel` -> PCBM scaled-space maps (how the body renders)
* `DiscoveryInfo.SetLevel` -> name and orbit data in Tracking Station / map
* `GetVisualLevelForBody` -> Parallax surface detail
* orbit renderer visibility
* plus contract gating via `KASABodyDiscoveryStage`

They do **not** touch science or resource availability.

So: **only a visual scan should change how a body looks.** Altimetry,
biome and resource are *data*, not *sight*. They become capability gates,
not appearance gates.

This is not just thematically tidy. SCANsat's visual scan is what makes
SCANsat sample the body's actual scaled-space texture when drawing its
maps — the same texture KASA's stages control. Visual scan and visual
reveal are mechanically the same thing.

---

## 3. Revised ladder

Unchanged 0-4. Only 5 and 6 change their trigger.

| Stage | Name | Trigger | Now driven by |
|---|---|---|---|
| 0 | Hidden | — | |
| 1 | Presence | crew sighting / Sentinel | unchanged |
| 2 | (unused) | — | |
| 3 | Telescope | DP-01 | unchanged |
| 4 | Named | DP-02 | unchanged |
| 5 | **Seen** | **Lo-res visual 80%** | recon contract |
| 6 | **Fully mapped** | **Hi-res visual 80%** | survey contract |

Stage 5 reads as "we finally have pictures — low detail, but we can see
what it is." Stage 6 is full detail and Parallax terrain.

**Ordering is now guaranteed by contract separation**, not by hoping one
scanner is slower than another. Lo-res belongs to a contract that must be
completed before the survey contract is offered.

---

## 4. The two contracts, per body

**Organising principle: the lo-res / hi-res divide IS the recon / survey
divide.** SCANsat scan types are independent bits with no overlap — a
hi-res scanner does NOT satisfy a lo-res requirement (`scansat-sar-paz-1`
is sensorType 2 only; `scansat-recon-ikonos-1` is 64 only). So each pass
needs its own parts, and contracts MUST state which resolution is wanted.

### Recon — the lo-res pass (cheap, early)
*"We know something is there. We do not know if it is worth the money."*

* **Lo-res altimetry** 80% coverage
* **Lo-res visual** 80% coverage -> **stage 5**
* **Biome** 80% coverage (arrives free with the MODIS part)
* Requirement: body at stage >= 4 (named)

Scanners: ~3,300 (RADAR/Poseidon) + ~3,000 (MODIS) = **~6,300 funds**,
both at `basicScience`.

**Biome note:** `scansat-multi-modis-1` (sensorType 12) provides lo-res
visual AND biome in one part, so biome coverage happens whether we ask for
it or not. Better an explicit objective than a silent one. Biome no longer
advances any stage — it gates biome science and landing-site choice.

### Survey — the hi-res pass (expensive, the flagship stack)
*"Now we know where to land and what is down there."*

* **Hi-res altimetry** 80% coverage
* **Hi-res visual** 80% coverage -> **stage 6**
* **Resource** 80% coverage -> resource reveal (`KASAResourceScanned`)
* Requirement: recon contract complete

Scanners: ~8,000 (SAR/PAZ) + ~7,500 (IKONOS) + ~15,000 (CRISM/MISE) =
**~30,500 funds**, at `advElectrics` / `precisionEngineering` /
`advExploration`.

This is the multi-scanner stack players actually fly — one launcher,
several probes released at each scanner's optimal altitude.

### Contract wording
Always state the resolution explicitly: "Achieve 80% **lo-res altimetry**
coverage", never just "altimetry coverage" — the player cannot otherwise
tell which scanner to bring, and the wrong one will never tick.

**Never name specific parts in contract text.** Scan TYPE only. Any part
providing that SCANtype qualifies, including modded and future ones, so
naming a part would be both wrong and fragile. Part names in section 5 are
a build-time reference for us, not player-facing.

### Settled parameters
* **All coverage thresholds stay at 80%**, both contracts, every scan.
  Tweak later if playtesting suggests otherwise.
* **Rewards stay tight for both.** Funds pressure is deliberate: it keeps
  the tourism contracts relevant and pushes the real payoff out to the
  resource economy.

## 5. Part reference

SCANtype bitmask, confirmed from the installed part configs and
cross-checked against SCANsat's own docs (stock MULTI = 24 = 8+16 =
Biome + Anomaly):

| Bit | Value | Scan |
|---|---|---|
| 2^0 | 1 | AltimetryLoRes |
| 2^1 | 2 | AltimetryHiRes |
| 2^2 | **4** | **VisualLoRes** |
| 2^3 | 8 | Biome |
| 2^4 | 16 | Anomaly |
| 2^5 | 32 | AnomalyDetail (BTDT) |
| 2^6 | **64** | **VisualHiRes** |
| 2^7 | 128 | FuzzyResources |
| 2^8 | 256 | Resources |

Parts providing the visual tiers:

| Scan | Part | sensorType | Tech node | Cost | Best alt |
|---|---|---|---|---|---|
| VisualLoRes | `scansat-multi-modis-1` | 12 | basicScience | 3,000 | 70 km |
| VisualLoRes | `scansat-multi-abi-1` | 140 | spaceExploration | 10,000 | 300 km |
| VisualLoRes | `scansat-multi-msi-1` | 140 | advUnmanned | 15,000 | 500 km |
| VisualHiRes | `scansat-recon-ikonos-1` | 64 | precisionEngineering | 7,500 | 70 km |
| VisualHiRes | `scansat-recon-worldview-3-1` | 80 | unmannedTech | 18,400 | 350 km |
| VisualHiRes | `scansat-recon-kh11-1` | 80 | largeUnmanned | 25,000 | 200 km |

The cost curve carries the narrative by itself: 3,000 funds to go and look,
7,500-25,000 to map it properly.

---

## 6. SCANsat version dependency  *(noted, accepted)*

The **stock** SCANsat parts (RADAR, SAR, MULTI, BTDT) provide **no visual
scanning at all** — MULTI is biome + anomaly only. Visual gating depends
entirely on the expanded part set (`scansat-multi-*`, `scansat-recon-*`)
shipped with current SCANsat.

Consequence: a player on an older SCANsat gets recon/survey contracts they
can **never complete**. `KASASCANsatCoverage` fails open only when SCANsat
is absent *entirely*, not when it is present but lacking these parts.

**Decision: accept and document.** There is no good reason to run an
outdated SCANsat. To be stated in the mod's README/requirements as a
minimum SCANsat version.

---

## 7. Removing `PartUnlocked` requirements  *(agreed)*

Six `PartUnlocked` requirements exist (02: 3, 03: 1, 04: 2), gating
contracts on owning the relevant scanner.

Removed, deliberately. Rationale: seeing a contract you cannot yet fly
tells the player *which tech node to aim for* and gives purpose to
collecting science. Nothing breaks — the SCANsat coverage requirement
still gates **completion**, so an unequipped player simply cannot finish
it yet.

---

## 8. Rollout

Nine probe contracts currently advance stage 5/6 and need splitting:

| File | Contract | Body |
|---|---|---|
| 04 | `KSP_01_MunProbe` | Mun |
| 04 | `KSP_03_MinmusProbe` | Minmus |
| 05 | `IP_01_Probe` | Moho |
| 05 | `IP_02_Probe` | Eve |
| 06 | `OP_01_Probe` | Duna |
| 06 | `OP_05_Probe` | Ike |
| 06 | `OP_07_Probe` | Dres |
| 06 | `OP_10_Probe` | Laythe |
| 06 | `OP_15_Probe` | Eeloo |

`OP_09_Probe` (Jool) does not advance stage 5/6 and is out of scope.

**Jool gotcha:** SCANsat biome scanning only works on bodies that *have*
biomes — every stock body except the Sun and Jool. If Jool ever gets a
recon contract, it must **not** carry a biome coverage parameter, or the
contract can never complete. Same caution for any future gas giant.

Suggested naming, following existing convention:
`KSP_01a_MunRecon` / `KSP_01_MunProbe` (survey), etc. Exact names to be
settled at build time — **contract names must be verified against the files
before use**, since fabricated names have caused errors before.

---

## 9. Save impact

* Splitting one contract into two adds new `CONTRACT_TYPE` names. An
  in-progress save with the old contract active will lose it.
* Bodies already at stage 6 stay there — `AdvanceBodyToStage` never
  regresses, so no body will visibly "un-reveal".
* Stephen is mid-playthrough at the Mun resource scan, so **the Mun and
  Minmus split will affect the current save.** Decide whether to restart
  or accept the disruption before building.

---

## 10. Open items

* Exact funds/science/rep numbers for the two contracts (kept tight —
  see section 4).
* Contract names, to be verified against the files at build time.

Settled since first draft: coverage stays at 80% everywhere; recon takes a
lo-res altimetry pass rather than being purely visual; rewards deliberately
cheap for both; save disruption accepted (restart is fine).
