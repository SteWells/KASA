// ================================================================
// KASA — Body Discovery System
// KASADiscovery.cs
// ================================================================
// Adapted from ResearchBodies by Jamie Leighton (MIT License)
// https://github.com/JPLRepo/ResearchBodies
//
// This plugin implements body masking for KASA. At the start of
// a new career, all celestial bodies not listed in KASA_Discovery.cfg
// are hidden from the tracking station (set to DiscoveryLevels.Presence).
// Bodies are revealed automatically when a vessel enters their SOI,
// or when a contract explicitly discovers them.
//
// Moons of unvisited planets are hidden until the parent planet's
// SOI has been entered — you have to go there to find them.
//
// WHAT THIS FILE CONTAINS:
//   KASADiscoveryScenario   — ScenarioModule, saves/loads state,
//                             handles SOI change events
//   KASADiscoveryAddon      — KSPAddon, initialises new careers,
//                             loads config from KASA_Discovery.cfg
//   KASABodyDiscoveredReq   — Contract Configurator requirement,
//                             allows contracts to gate on whether
//                             a specific body has been discovered
//
// TO COMPILE:
//   Reference: Assembly-CSharp.dll, UnityEngine.dll,
//              ContractConfigurator.dll (from your KSP install)
//   Target framework: .NET Framework 4.7.2 (matches KSP and ContractConfigurator)
//   Output: KASA.dll → GameData/KASA/Plugins/KASA.dll
// ================================================================

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Contracts;              // ContractParameter, ParameterState
using ContractConfigurator;   // ContractBehaviour, ContractRequirement, ConfiguredContract

namespace KASA
{
    // ================================================================
    // SCENARIO MODULE
    // Persists discovery state in the save game. One instance per save.
    // ================================================================
    [KSPScenario(
        ScenarioCreationOptions.AddToNewCareerGames |
        ScenarioCreationOptions.AddToExistingCareerGames,
        GameScenes.SPACECENTER, GameScenes.FLIGHT, GameScenes.TRACKSTATION)]
    public class KASADiscoveryScenario : ScenarioModule
    {
        // ---- Singleton ----
        public static KASADiscoveryScenario Instance { get; private set; }

        // body.bodyName → has been discovered
        // Discovery stages per body (authoritative mapping in GetTargetLevel):
        //   0 = hidden — never seen                       (None)
        //   1 = crew spotted in sky — unknown blob        (Presence)
        //   2 = intermediate — unused                     (Presence)
        //   3 = telescope detection — still unknown       (Presence)
        //   4 = detailed observation — name revealed      (Appearance)
        //   5 = altimetry scan — orbit details            (StateVectors)
        //   6 = biome scan — full info                    (Owned)
        public Dictionary<string, int> BodyDiscovered = new Dictionary<string, int>();
        public Dictionary<string, bool> BodyResourceScanned = new Dictionary<string, bool>();
        // DESIGN-003: fuels whose ENGINES + TANKS have been revealed. Set by the
        // KASAFuelUnlocked behaviour on completion of the crewed sample-return
        // contract for that fuel's source body. Separate from BodyResourceScanned:
        // scanning a body unlocks its DRILL, returning a sample unlocks its FUEL.
        public HashSet<string> UnlockedFuels = new HashSet<string>();

        // Sentinel survey system
        public bool SentinelActive = false;
        public double SentinelActivationTime = 0;

        // Outer survey system (Gazer relocated beyond Kerbin -> direct imaging).
        // Reveals bodies EXTERIOR to Kerbin, which never transit and so are
        // invisible to the Sentinel.
        public bool OuterSurveyActive = false;
        public double OuterSurveyActivationTime = 0;

        // Scheduled planet detection times (bodyName -> UT when it will be revealed at level 1)
        public Dictionary<string, double> PlanetDetectionTimers = new Dictionary<string, double>();

        // Scheduled moon discovery times (moonName -> UT when it reveals)
        public Dictionary<string, double> MoonDiscoveryTimers = new Dictionary<string, double>();

        // Moon -> parent planet mapping (populated from KASA_Discovery.cfg)
        public static Dictionary<string, string> MoonParents = new Dictionary<string, string>();

        // Planet orbital radii for weighting (SMA in metres, populated from config)
        public static Dictionary<string, double> PlanetSMA = new Dictionary<string, double>();

        // ----------------------------------------------------------------
        // Lifecycle
        // ----------------------------------------------------------------
        public override void OnAwake()
        {
            base.OnAwake();
            Instance = this;
            GameEvents.onVesselSOIChanged.Add(OnSOIChanged);
            GameEvents.onLevelWasLoaded.Add(OnSceneLoaded);
            // Subscribe to MapView exit so we re-hide bodies the instant the
            // player closes map view during flight, rather than waiting for the
            // next poll tick. Without this, hidden bodies briefly pop back into
            // the in-flight view as KSP re-enables scaledBody objects during the
            // camera transition out of map mode.
            MapView.OnExitMapView += OnExitMapView;
            Debug.Log("[KASA] KASADiscoveryScenario awakened.");
        }

        void OnDestroy()
        {
            GameEvents.onVesselSOIChanged.Remove(OnSOIChanged);
            GameEvents.onLevelWasLoaded.Remove(OnSceneLoaded);
            MapView.OnExitMapView -= OnExitMapView;
            if (Instance == this) Instance = null;
        }

        // Called the instant the player closes map view in the flight scene.
        private void OnExitMapView()
        {
            if (BodyDiscovered.Count > 0)
                ApplyDiscoveryLevels();
        }

        // Re-apply discovery levels every time a relevant scene loads.
        // KSP resets DiscoveryInfo on scene transitions — we must override.
        private void OnSceneLoaded(GameScenes scene)
        {
            if (scene == GameScenes.SPACECENTER ||
                scene == GameScenes.TRACKSTATION ||
                scene == GameScenes.FLIGHT)
            {
                StartCoroutine(ApplyAfterDelay());
            }
        }

        // LateUpdate runs every frame AFTER KSP's own Update(), so any
        // MapObject or scaledBody KSP re-enables in its Update is immediately
        // suppressed again before the frame renders. This is why a coroutine
        // poll couldn't fully solve the label pop-in — there was always a
        // window between ticks where KSP had re-enabled the object and the
        // frame had already rendered.
        //
        // We only touch objects that have been incorrectly re-enabled (i.e.
        // active when they should be hidden) to keep this as cheap as possible
        // — no work done for discovered bodies, no work when nothing has changed.
        private void LateUpdate()
        {
            if (!HighLogic.LoadedSceneIsFlight) return;
            if (FlightGlobals.Bodies == null) return;

            foreach (CelestialBody body in FlightGlobals.Bodies)
            {
                if (body == null) continue;
                if (GetStage(body.bodyName) > 0) continue;

                if (body.MapObject != null && body.MapObject.gameObject.activeSelf)
                    body.MapObject.gameObject.SetActive(false);

                if (body.scaledBody != null && body.scaledBody.activeSelf)
                    body.scaledBody.SetActive(false);

                if (body.orbitDriver != null &&
                    body.orbitDriver.Renderer != null &&
                    body.orbitDriver.Renderer.enabled)
                    body.orbitDriver.Renderer.enabled = false;
            }
        }

        // ----------------------------------------------------------------
        // Save / Load
        // ----------------------------------------------------------------
        public override void OnSave(ConfigNode node)
        {
            foreach (var kvp in BodyDiscovered)
            {
                ConfigNode bodyNode = node.AddNode("BODY");
                bodyNode.AddValue("name", kvp.Key);
                bodyNode.AddValue("stage", kvp.Value);
            }
            foreach (var kvp in BodyResourceScanned)
            {
                ConfigNode resNode = node.AddNode("BODY_RESOURCE");
                resNode.AddValue("name", kvp.Key);
                resNode.AddValue("scanned", kvp.Value.ToString());
            }
            foreach (string fuel in UnlockedFuels)
            {
                ConfigNode fn = node.AddNode("FUEL_UNLOCK");
                fn.AddValue("fuel", fuel);
            }
            // Sentinel state
            node.AddValue("sentinelActive", SentinelActive);
            node.AddValue("sentinelActivationTime", SentinelActivationTime);
            node.AddValue("outerSurveyActive", OuterSurveyActive);
            node.AddValue("outerSurveyActivationTime", OuterSurveyActivationTime);
            foreach (var kvp in PlanetDetectionTimers)
            {
                ConfigNode pn = node.AddNode("PLANET_TIMER");
                pn.AddValue("name", kvp.Key);
                pn.AddValue("time", kvp.Value);
            }
            foreach (var kvp in MoonDiscoveryTimers)
            {
                ConfigNode mn = node.AddNode("MOON_TIMER");
                mn.AddValue("name", kvp.Key);
                mn.AddValue("time", kvp.Value);
            }
            Debug.Log("[KASA] Discovery state saved for " + BodyDiscovered.Count + " bodies.");
        }

        public override void OnLoad(ConfigNode node)
        {
            BodyDiscovered.Clear();
            foreach (ConfigNode bodyNode in node.GetNodes("BODY"))
            {
                string name = "";
                int stage = 0;
                bodyNode.TryGetValue("name", ref name);
                bodyNode.TryGetValue("stage", ref stage);
                if (!string.IsNullOrEmpty(name))
                    BodyDiscovered[name] = stage;
            }
            foreach (ConfigNode resNode in node.GetNodes("BODY_RESOURCE"))
            {
                string name = "";
                bool scanned = false;
                resNode.TryGetValue("name", ref name);
                resNode.TryGetValue("scanned", ref scanned);
                if (!string.IsNullOrEmpty(name))
                    BodyResourceScanned[name] = scanned;
            }
            UnlockedFuels.Clear();
            foreach (ConfigNode fn in node.GetNodes("FUEL_UNLOCK"))
            {
                string fuel = "";
                fn.TryGetValue("fuel", ref fuel);
                if (!string.IsNullOrEmpty(fuel)) UnlockedFuels.Add(fuel);
            }
            // Sentinel state
            node.TryGetValue("sentinelActive", ref SentinelActive);
            node.TryGetValue("sentinelActivationTime", ref SentinelActivationTime);
            node.TryGetValue("outerSurveyActive", ref OuterSurveyActive);
            node.TryGetValue("outerSurveyActivationTime", ref OuterSurveyActivationTime);
            foreach (ConfigNode pn in node.GetNodes("PLANET_TIMER"))
            {
                string name = ""; double time = 0;
                pn.TryGetValue("name", ref name);
                pn.TryGetValue("time", ref time);
                if (!string.IsNullOrEmpty(name)) PlanetDetectionTimers[name] = time;
            }
            foreach (ConfigNode mn in node.GetNodes("MOON_TIMER"))
            {
                string name = ""; double time = 0;
                mn.TryGetValue("name", ref name);
                mn.TryGetValue("time", ref time);
                if (!string.IsNullOrEmpty(name)) MoonDiscoveryTimers[name] = time;
            }
            Debug.Log("[KASA] Discovery state loaded for " + BodyDiscovered.Count + " bodies.");

            if (BodyDiscovered.Count == 0)
            {
                // No saved state — this is a new career. Initialise after a short
                // delay so FlightGlobals.Bodies is populated before we apply levels.
                Debug.Log("[KASA] No saved discovery state found — initialising new career.");
                StartCoroutine(InitialiseNewCareerAfterDelay());
            }
            else
            {
                StartCoroutine(ApplyAfterDelay());
            }
        }

        private IEnumerator InitialiseNewCareerAfterDelay()
        {
            yield return null;
            yield return null;
            InitialiseNewCareer(KASADiscoveryAddon.KnownBodiesAtStart);
        }

        // ----------------------------------------------------------------
        // Initialise a brand new career
        // Called by KASADiscoveryAddon when a new game is created
        // ----------------------------------------------------------------
        public void InitialiseNewCareer(List<string> knownBodies)
        {
            BodyDiscovered.Clear();
            foreach (CelestialBody body in FlightGlobals.Bodies)
            {
                if (body == null) continue;
                // Known bodies start at stage 6 (Owned) — fully characterised
                // Hidden bodies start at stage 0 (None) — completely invisible
                int stage = knownBodies.Contains(body.bodyName) ? 6 : 0;
                BodyDiscovered[body.bodyName] = stage;
                Debug.Log("[KASA] Career init: " + body.bodyName + " = stage " + stage);
            }
            ApplyDiscoveryLevels();
        }

        // ----------------------------------------------------------------
        // Apply DiscoveryInfo levels to all bodies based on current state.
        // Always applies our saved stage — overrides whatever KSP thinks.
        //
        // Three layers of hiding for stage-0 bodies:
        //   1. DiscoveryInfo.SetLevel(None)  — hides info in tracking station
        //   2. MapObject.gameObject.SetActive(false) — hides tracking station dot
        //   3. ScaledSpace + orbit renderer disabled — hides from in-flight map
        // ----------------------------------------------------------------
        public void ApplyDiscoveryLevels()
        {
            if (FlightGlobals.Bodies == null) return;
            foreach (CelestialBody body in FlightGlobals.Bodies)
            {
                if (body == null || body.DiscoveryInfo == null) continue;

                int stage = GetStage(body.bodyName);
                DiscoveryLevels target = GetTargetLevel(body.bodyName);
                bool visible = stage > 0;

                // Layer 1: DiscoveryInfo (tracking station list, tab/alt-tab)
                body.DiscoveryInfo.SetLevel(target);

                // Layer 2: MapObject (tracking station dot)
                if (body.MapObject != null)
                    body.MapObject.gameObject.SetActive(visible);

                // Layer 3: Orbit renderer
                if (body.orbitDriver != null && body.orbitDriver.Renderer != null)
                    body.orbitDriver.Renderer.enabled = visible;

                // Layer 4: Visual appearance via ProgressiveCBMaps
                // This replaces the old scaledBody.SetActive(false) approach —
                // instead of hiding the body entirely at stage 0, we set the
                // detail level. Stage 0 = invisible (level 0), higher stages
                // progressively reveal more detail.
                int visualLevel = DiscoveryStageToVisualLevel(stage);
                SetBodyVisualLevel(body, visualLevel);
            }
            Debug.Log("[KASA] Discovery levels applied to " + FlightGlobals.Bodies.Count + " bodies.");
        }

        /// <summary>
        /// Called periodically to check Sentinel detection timers and moon
        /// discovery timers. Fires reveals when the scheduled UT has passed.
        /// Should be called from a FixedUpdate or coroutine in the Addon.
        /// </summary>
        public void UpdateSentinelTimers()
        {
            if (!SentinelActive && !OuterSurveyActive) return;

            double now = Planetarium.GetUniversalTime();

            // Check planet detection timers
            List<string> toReveal = new List<string>();
            foreach (var kvp in PlanetDetectionTimers)
                if (now >= kvp.Value) toReveal.Add(kvp.Key);

            foreach (string bodyName in toReveal)
            {
                PlanetDetectionTimers.Remove(bodyName);
                if (GetDiscoveryStage(bodyName) > 0) continue; // already revealed

                // TRANSIT RULE: the Sentinel (transit) can only reveal interior
                // bodies; exterior bodies belong to the outer survey (imaging).
                // If an exterior body is pending here while the outer survey is
                // inactive, it leaked from the old detect-everything logic --
                // drop it silently. ActivateOuterSurvey re-seeds it properly
                // once the Gazer is relocated.
                bool interior = IsInteriorToKerbin(bodyName);
                if (!interior && !OuterSurveyActive) continue;

                // Set to stage 1 — grey blob visible
                BodyDiscovered[bodyName] = 1;
                ApplyDiscoveryLevels();

                // DESIGN-001/002: report a detection only, do NOT name the body
                // (name resolves at stage 4). Message depends on detection method.
                string detectMsg = interior
                    ? "[KASA] Wernher: \"The Sentinel has logged a transit event — an uncharted body " +
                      "in solar orbit, something we have not catalogued before. We cannot identify it " +
                      "yet; the team is working the orbital data now. Once we have pinned down its " +
                      "orbit, I will know what we are dealing with.\""
                    : "[KASA] Wernher: \"The survey telescope has caught a slow mover in the deep field — " +
                      "a faint point that has shifted against the background stars over successive frames. " +
                      "That motion marks it as one of ours: a world orbiting beyond Kerbin. We cannot resolve " +
                      "it yet, but the team is working the astrometry. Once we have its orbit, I will know what it is.\"";

                ScreenMessages.PostScreenMessage(detectMsg, 12f, ScreenMessageStyle.UPPER_LEFT);

                Debug.Log("[KASA] " + (interior ? "Sentinel (transit)" : "Outer survey (imaging)") +
                          " detected: " + bodyName);
            }

            // Check moon discovery timers
            List<string> moonsToReveal = new List<string>();
            foreach (var kvp in MoonDiscoveryTimers)
                if (now >= kvp.Value) moonsToReveal.Add(kvp.Key);

            foreach (string moonName in moonsToReveal)
            {
                MoonDiscoveryTimers.Remove(moonName);
                if (GetDiscoveryStage(moonName) > 0) continue;

                BodyDiscovered[moonName] = 1;
                ApplyDiscoveryLevels();

                ScreenMessages.PostScreenMessage(
                    "[KASA] Wernher: \"Our orbital data has resolved a second body in the same system — " +
                    "it appears to be a moon of the object we are already tracking. Another one for the analysis queue.\"",
                    10f, ScreenMessageStyle.UPPER_LEFT);

                Debug.Log("[KASA] Moon revealed: " + moonName);
            }
        }

        private int GetStage(string bodyName)
        {
            int stage;
            return BodyDiscovered.TryGetValue(bodyName, out stage) ? stage : 0;
        }

        // Heliocentric SMA of the home body (Kerbin). The Sentinel orbits
        // Kerbin, so this is the transit observer's distance from Kerbol.
        private double GetHomeSMA()
        {
            CelestialBody home = FlightGlobals.GetHomeBody();
            return (home != null && home.orbit != null)
                ? home.orbit.semiMajorAxis
                : 13599840256.0; // stock Kerbin SMA fallback
        }

        // Transit-detectable (Sentinel) only if interior to the observer.
        private bool IsInteriorToKerbin(string bodyName)
        {
            double sma;
            if (!PlanetSMA.TryGetValue(bodyName, out sma)) return false;
            return sma < GetHomeSMA();
        }

        private DiscoveryLevels GetTargetLevel(string bodyName)
        {
            if (!BodyDiscovered.ContainsKey(bodyName))
                return DiscoveryLevels.None;   // completely unknown — hide entirely

            switch (BodyDiscovered[bodyName])
            {
                case 0: return DiscoveryLevels.None;        // hidden, never seen
                case 1: return DiscoveryLevels.Presence;    // crew spotted in sky — unknown blob
                case 2: return DiscoveryLevels.Presence;    // intermediate (unused)
                case 3: return DiscoveryLevels.Presence;    // telescope detection — still unknown
                case 4: return DiscoveryLevels.Appearance;  // detailed observation — name revealed
                case 5: return DiscoveryLevels.StateVectors;// altimetry scan — orbit details
                case 6: return DiscoveryLevels.Owned;       // biome scan — full info
                default: return DiscoveryLevels.None;
            }
        }

        private IEnumerator ApplyAfterDelay()
        {
            // Wait for KSP's own body initialisation to finish
            yield return null;
            yield return null;
            yield return new WaitForSeconds(0.5f);
            ApplyDiscoveryLevels();
            // Apply a second time after a further delay — KSP occasionally
            // re-initialises discovery info when loading scene UI elements
            yield return new WaitForSeconds(1.0f);
            ApplyDiscoveryLevels();
        }

        // ----------------------------------------------------------------
        // SOI change event — the core discovery trigger
        // ----------------------------------------------------------------
        private void OnSOIChanged(GameEvents.HostedFromToAction<Vessel, CelestialBody> evt)
        {
            CelestialBody toBody = evt.to;
            if (toBody == null) return;

            // Skip bodies that are already known
            if (IsDiscovered(toBody.bodyName)) return;

            // Discover this body
            DiscoverBody(toBody.bodyName);

            // If this is a moon, entering the parent planet's SOI reveals
            // that the parent exists — but only if the player hasn't already
            // visited the parent directly.
            if (toBody.referenceBody != null &&
                toBody.referenceBody != toBody &&
                GetDiscoveryStage(toBody.referenceBody.bodyName) < 1)
            {
                DiscoverBody(toBody.referenceBody.bodyName);
                Debug.Log("[KASA] Parent body " + toBody.referenceBody.bodyName +
                          " revealed (stage 1) via moon SOI entry.");
            }

            // If this is a planet (not a moon of it), start moon discovery timers
            // so undiscovered moons are revealed over the next N*3 days.
            if (toBody.referenceBody != null &&
                toBody.referenceBody.bodyName == "Sun" ||
                toBody.referenceBody == Planetarium.fetch.Sun)
            {
                StartMoonDiscovery(toBody.bodyName);
            }

            // Screen message
            string msg = string.Format(
                "KASA Mission Control: Vessel has entered the sphere of influence of {0}. New body discovered!",
                toBody.GetDisplayName());
            ScreenMessages.PostScreenMessage(msg, 6f, ScreenMessageStyle.UPPER_CENTER);

            Debug.Log("[KASA] Body discovered via SOI entry: " + toBody.bodyName);
        }

        // ----------------------------------------------------------------
        // Public API for contracts and other systems
        // ----------------------------------------------------------------

        /// <summary>
        /// Returns true if this body has been discovered.
        /// </summary>
        /// <summary>Returns true if this body has been discovered (stage >= 1).</summary>
        public bool IsDiscovered(string bodyName)
        {
            int stage;
            return BodyDiscovered.TryGetValue(bodyName, out stage) && stage >= 1;
        }

        public bool IsResourceScanned(string bodyName)
        {
            bool scanned;
            return BodyResourceScanned.TryGetValue(bodyName, out scanned) && scanned;
        }

        public void MarkResourceScanned(string bodyName)
        {
            if (string.IsNullOrEmpty(bodyName)) return;
            BodyResourceScanned[bodyName] = true;
            Debug.Log("[KASA] Resource scan complete for: " + bodyName);
            UnlockResourcesForBody(bodyName);
        }

        // ----------------------------------------------------------------
        // DESIGN-003 — fuel part reveal
        // ----------------------------------------------------------------

        /// <summary>True once this fuel's engines and tanks have been revealed.</summary>
        public bool IsFuelUnlocked(string fuel)
        {
            return !string.IsNullOrEmpty(fuel) && UnlockedFuels.Contains(fuel);
        }

        /// <summary>Reveal a fuel's parts. Idempotent — safe to call repeatedly
        /// (contract completion can re-fire on load).</summary>
        public void MarkFuelUnlocked(string fuel)
        {
            if (string.IsNullOrEmpty(fuel)) return;
            if (!UnlockedFuels.Add(fuel)) return;          // already unlocked
            Debug.Log("[KASA] Fuel unlocked: " + fuel + " — revealing its parts.");
            KASAPartGate.Reveal(fuel);
        }

        // ----------------------------------------------------------------
        // Sentinel survey system
        // ----------------------------------------------------------------

        /// <summary>
        /// Called by KASAStartSentinelSurvey behaviour when the Sentinel
        /// deployment contract completes. Schedules planet detection timers
        /// weighted by orbital proximity to Kerbol.
        /// </summary>
        public void ActivateSentinel()
        {
            if (SentinelActive) return;
            SentinelActive = true;
            SentinelActivationTime = Planetarium.GetUniversalTime();
            Debug.Log("[KASA] Sentinel activated at UT " + SentinelActivationTime);

            double now = SentinelActivationTime;
            System.Random rng = new System.Random();

            // Schedule a detection timer for every undiscovered body that has
            // a configured SMA. Bodies closer to Kerbol get shorter timers.
            foreach (var kvp in PlanetSMA)
            {
                string bodyName = kvp.Key;
                if (GetDiscoveryStage(bodyName) > 0) continue; // already known
                if (PlanetDetectionTimers.ContainsKey(bodyName)) continue; // already scheduled

                double sma = kvp.Value;
                // TRANSIT RULE: the Sentinel detects by transit, so it can only
                // ever see bodies interior to the observer (Kerbin). Exterior
                // bodies never cross the sun's face from here -- they belong to
                // the outer survey. Skip them.
                if (sma >= GetHomeSMA()) continue;
                // Scale timer: at Moho distance (~5.3 Gm) = 5-15 days
                //              at Eeloo distance (~90 Gm) = 90-180 days
                // Linear interpolation in log space
                double minSMA = 5.3e9;
                double maxSMA = 90e9;
                double t = System.Math.Log(sma / minSMA) / System.Math.Log(maxSMA / minSMA);
                t = System.Math.Max(0, System.Math.Min(1, t));

                double minDays = 5 + t * 85;   // 5 days at Moho, 90 days at Eeloo
                double maxDays = 15 + t * 165;   // 15 days at Moho, 180 days at Eeloo

                double days = minDays + rng.NextDouble() * (maxDays - minDays);
                double detectionUT = now + days * KSPUtil.dateTimeFormatter.Day;

                PlanetDetectionTimers[bodyName] = detectionUT;
                Debug.Log("[KASA] Sentinel: " + bodyName + " scheduled for detection in " +
                          System.Math.Round(days, 1) + " days");
            }
        }

        /// <summary>
        /// Called by KASAStartOuterSurvey behaviour when the Gazer-relocation
        /// contract completes. Schedules detection timers for every undiscovered
        /// body EXTERIOR to Kerbin -- the outer planets, found by direct imaging
        /// (a slow point moving against the star field) rather than by transit.
        /// </summary>
        public void ActivateOuterSurvey()
        {
            if (OuterSurveyActive) return;
            OuterSurveyActive = true;
            OuterSurveyActivationTime = Planetarium.GetUniversalTime();
            Debug.Log("[KASA] Outer survey activated at UT " + OuterSurveyActivationTime);

            double now = OuterSurveyActivationTime;
            double homeSMA = GetHomeSMA();
            System.Random rng = new System.Random();

            foreach (var kvp in PlanetSMA)
            {
                string bodyName = kvp.Key;
                if (GetDiscoveryStage(bodyName) > 0) continue;             // already known (e.g. Duna, leaked earlier)
                if (PlanetDetectionTimers.ContainsKey(bodyName)) continue; // already scheduled

                double sma = kvp.Value;
                if (sma < homeSMA) continue; // interior bodies are the Sentinel's job

                // Direct imaging: nearer outer bodies resolve first.
                //   Duna (~20 Gm)  = 10-25 days
                //   Eeloo (~90 Gm) = 90-180 days
                double minSMA = 20e9;
                double maxSMA = 90e9;
                double t = System.Math.Log(System.Math.Max(sma, minSMA) / minSMA) /
                           System.Math.Log(maxSMA / minSMA);
                t = System.Math.Max(0, System.Math.Min(1, t));

                double minDays = 10 + t * 80;   // 10 days at Duna, 90 at Eeloo
                double maxDays = 25 + t * 155;  // 25 days at Duna, 180 at Eeloo
                double days = minDays + rng.NextDouble() * (maxDays - minDays);
                double detectionUT = now + days * KSPUtil.dateTimeFormatter.Day;

                PlanetDetectionTimers[bodyName] = detectionUT;
                Debug.Log("[KASA] Outer survey: " + bodyName + " scheduled for imaging in " +
                          System.Math.Round(days, 1) + " days");
            }
        }

        /// <summary>
        /// Called when a vessel enters a planet's SOI. Schedules moon discovery
        /// timers spread randomly within N*3 days where N is the moon count.
        /// </summary>
        public void StartMoonDiscovery(string parentBodyName)
        {
            CelestialBody parent = FlightGlobals.Bodies.Find(b => b.bodyName == parentBodyName);
            if (parent == null) return;

            // Find undiscovered moons
            List<string> undiscoveredMoons = new List<string>();
            foreach (CelestialBody body in FlightGlobals.Bodies)
            {
                if (body.referenceBody != parent) continue;
                if (body == parent) continue;
                if (GetDiscoveryStage(body.bodyName) > 0) continue;
                if (MoonDiscoveryTimers.ContainsKey(body.bodyName)) continue;
                undiscoveredMoons.Add(body.bodyName);
            }

            if (undiscoveredMoons.Count == 0) return;

            double now = Planetarium.GetUniversalTime();
            double windowDays = undiscoveredMoons.Count * 3.0;
            double windowSeconds = windowDays * KSPUtil.dateTimeFormatter.Day;

            // Generate sorted random times within the window
            System.Random rng = new System.Random();
            List<double> times = new List<double>();
            foreach (string _ in undiscoveredMoons)
                times.Add(now + rng.NextDouble() * windowSeconds);
            times.Sort();

            for (int i = 0; i < undiscoveredMoons.Count; i++)
            {
                MoonDiscoveryTimers[undiscoveredMoons[i]] = times[i];
                Debug.Log("[KASA] Moon " + undiscoveredMoons[i] + " scheduled for discovery in " +
                          System.Math.Round((times[i] - now) / KSPUtil.dateTimeFormatter.Day, 1) + " days");
            }
        }

        // ----------------------------------------------------------------
        // ProgressiveCBMaps integration
        // ----------------------------------------------------------------
        private bool _pcbmInitialised = false;
        private bool _pcbmAvailable = false;
        // Cache last applied visual level per body — avoids calling setVisualLevel
        // every frame when nothing has changed (reduces EVE NullRef spam from PCBM)
        private Dictionary<string, int> _lastAppliedLevel = new Dictionary<string, int>();
        // Cached reflection references
        private System.Type _pcbmVisualMapsType;
        private object _pcbmInstance;
        private System.Type _pcbmCBInfoType;
        private System.Reflection.MethodInfo _pcbmGetInfoDict;
        private System.Reflection.MethodInfo _pcbmSetLevel;

        private bool InitPCBM()
        {
            if (_pcbmInitialised) return _pcbmAvailable;
            _pcbmInitialised = true;

            // Find ProgressiveCBMaps.VisualMaps type
            _pcbmVisualMapsType = null;
            AssemblyLoader.loadedAssemblies.TypeOperation(t =>
            {
                if (t.FullName == "ProgressiveCBMaps.VisualMaps")
                    _pcbmVisualMapsType = t;
            });

            if (_pcbmVisualMapsType == null)
            {
                Debug.Log("[KASA] ProgressiveCBMaps not found — using SetActive fallback.");
                return false;
            }

            // Get Instance
            var instanceField = _pcbmVisualMapsType.GetField("Instance",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            _pcbmInstance = instanceField?.GetValue(null);

            if (_pcbmInstance == null)
            {
                Debug.Log("[KASA] ProgressiveCBMaps Instance is null.");
                return false;
            }

            // Find CelestialBodyInfo type
            AssemblyLoader.loadedAssemblies.TypeOperation(t =>
            {
                if (t.FullName == "ProgressiveCBMaps.CelestialBodyInfo")
                    _pcbmCBInfoType = t;
            });

            // Get CBVisualMapsInfo property getter
            _pcbmGetInfoDict = _pcbmVisualMapsType.GetMethod("get_CBVisualMapsInfo",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

            // Get setVisualLevel method on CelestialBodyInfo
            if (_pcbmCBInfoType != null)
                _pcbmSetLevel = _pcbmCBInfoType.GetMethod("setVisualLevel",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

            _pcbmAvailable = (_pcbmGetInfoDict != null && _pcbmSetLevel != null);
            Debug.Log("[KASA] ProgressiveCBMaps available: " + _pcbmAvailable);
            return _pcbmAvailable;
        }

        /// <summary>
        /// Sets the visual detail level for a body via ProgressiveCBMaps.
        /// Falls back to SetActive(false) for level 0 if PCBM is not available.
        /// </summary>
        /// <summary>Forget which levels were last applied, so the next ApplyDiscoveryLevels()
        /// re-applies every body from scratch. Safety net: called on scene load so a body
        /// whose texture was disposed can never stay broken beyond the current scene, even
        /// if the per-body detection above misses the case.</summary>
        public void InvalidateVisualLevelCache()
        {
            _lastAppliedLevel.Clear();
        }

        /// <summary>True if the body's scaled-space material has lost its main texture.
        /// This is the SCANsat-disposal case: the material survives but _MainTex is null
        /// (or a destroyed Unity object, which compares equal to null), so the body renders
        /// black. Returns false on anything unexpected — a false negative just means we skip
        /// a repair, whereas a false positive would re-apply PCBM every single frame.</summary>
        private bool ScaledTextureMissing(CelestialBody body)
        {
            try
            {
                if (body == null || body.scaledBody == null) return false;

                var mr = body.scaledBody.GetComponent<MeshRenderer>();
                if (mr == null || mr.sharedMaterial == null) return false;

                Material mat = mr.sharedMaterial;
                if (!mat.HasProperty("_MainTex")) return false;

                // Unity overloads == so a destroyed texture compares equal to null here.
                return mat.GetTexture("_MainTex") == null;
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning("[KASA] ScaledTextureMissing check failed for " +
                                 (body != null ? body.bodyName : "null") + ": " + ex.Message);
                return false;
            }
        }

        public void SetBodyVisualLevel(CelestialBody body, int level)
        {
            if (body == null) return;

            // The Sun has no scaledBody textures in PCBM — skip it entirely.
            if (body == Planetarium.fetch.Sun || body.bodyName == "Sun") return;

            // Skip the PCBM call if the level hasn't changed — avoids hammering it.
            // Parallax suppression is handled by Parallax itself querying KASA via
            // GetVisualLevelForBody on each scene's ScaledManager.Start().
            //
            // BUT: the cache must not block a REPAIR. SCANsat disposes of a body's
            // scaled-space texture when you close/switch its map (it runs its unload
            // path even with Kopernicus on-demand loading disabled). For a body PCBM
            // has given a generated texture — i.e. any partially-revealed body — nothing
            // regenerates it, so the material is left with a dead _MainTex and the body
            // renders as a plain black circle. Without the check below the cache would
            // then refuse to re-apply (level unchanged), so the black state survived
            // scene changes indefinitely. If the texture has gone, re-apply regardless.
            int lastLevel;
            bool sameLevel = _lastAppliedLevel.TryGetValue(body.bodyName, out lastLevel) && lastLevel == level;
            bool repairing = sameLevel && ScaledTextureMissing(body);
            if (sameLevel && !repairing) return;
            _lastAppliedLevel[body.bodyName] = level;

            if (!InitPCBM())
            {
                // Fallback: level 0 = hidden, anything else = visible
                bool visible = level > 0;
                if (body.scaledBody != null)
                    body.scaledBody.SetActive(visible);
                if (body.MapObject != null)
                    body.MapObject.gameObject.SetActive(visible);
                return;
            }

            if (repairing)
                Debug.Log("[KASA] Scaled-space texture for " + body.bodyName +
                          " had been disposed (likely by a SCANsat map close/switch) — " +
                          "re-applying visual level " + level + " to restore it.");

            try
            {
                // Get the CBVisualMapsInfo dictionary
                var dict = _pcbmGetInfoDict.Invoke(_pcbmInstance, null) as System.Collections.IDictionary;
                if (dict == null) return;

                if (!dict.Contains(body)) return;
                var cbInfo = dict[body];
                _pcbmSetLevel.Invoke(cbInfo, new object[] { level });
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning("[KASA] SetBodyVisualLevel error for " + body.bodyName + ": " + ex.Message);
            }
        }

        // ----------------------------------------------------------------
        // Parallax Continued integration
        // ----------------------------------------------------------------
        // The KASA-aware build of Parallax queries GetVisualLevelForBody
        // (above) on each ScaledManager.Start() to decide whether to set up
        // its enhanced rendering for a body. Bodies at visual level < 4 are
        // skipped by Parallax entirely, leaving the stock material intact
        // so PCBM's low-detail texture renders normally.
        // No runtime intervention is needed from this side.
        // ----------------------------------------------------------------

        /// <summary>
        /// Returns the target PCBM level for a body based on its discovery stage.
        /// Stages map directly to PCBM levels (0-6) so each named stage corresponds
        /// to a specific visual detail level:
        ///   0 = invisible
        ///   1 = greyscale blob, low detail (FF-04 reveal: crew spots them in sky)
        ///   2 = greyscale, slight detail (intermediate — currently unused)
        ///   3 = greyscale, full shape (DP-01 telescope detection)
        ///   4 = colour returns, low detail (DP-02 detailed observation)
        ///   5 = colour, half detail (altimetry scan complete)
        ///   6 = full detail (biome scan complete)
        public static int DiscoveryStageToVisualLevel(int stage)
        {
            if (stage < 0) return 0;
            if (stage > 6) return 6;
            return stage;
        }

        /// <summary>
        /// Public static API for external mods (such as the KASA-aware build of
        /// Parallax) to query the current visual level of a body without taking
        /// a hard dependency on KASA. Returns 0-6 (0 = hidden, 6 = fully discovered).
        /// Returns 6 by default if the scenario is not yet loaded — fail-safe
        /// so external mods don't accidentally hide bodies when KASA isn't active.
        /// </summary>
        public static int GetVisualLevelForBody(string bodyName)
        {
            if (Instance == null) return 6;
            int stage = Instance.GetDiscoveryStage(bodyName);
            return DiscoveryStageToVisualLevel(stage);
        }

        /// <summary>Returns the current discovery stage (0-3) for a body.</summary>
        public int GetDiscoveryStage(string bodyName)
        {
            int stage;
            return BodyDiscovered.TryGetValue(bodyName, out stage) ? stage : 0;
        }

        /// <summary>
        /// Manually marks a body as discovered and sets its DiscoveryInfo to Owned.
        /// Call this from contract behaviours when an orbital survey reveals a body.
        /// </summary>
        /// <summary>
        /// Stage 1: SOI entered. Body becomes a blurred unknown object.
        /// Called automatically by OnSOIChanged.
        /// </summary>
        public void DiscoverBody(string bodyName)
        {
            int current = GetDiscoveryStage(bodyName);
            if (current >= 1) return; // already at this stage or higher
            SetBodyStage(bodyName, 1);
            Debug.Log("[KASA] Body entered SOI (stage 1 - Presence): " + bodyName);
        }

        /// <summary>
        /// Stage 1: Body first detected — shows as greyscale blob (PCBM level 1).
        /// Call from FF-04 completion to reveal Mun and Minmus as faint objects.
        /// </summary>
        public void RevealBodyPresence(string bodyName)
        {
            AdvanceBodyToStage(bodyName, 1);
        }

        /// <summary>
        /// Legacy: advances body to stage 3 (telescope/orbital detail level).
        /// </summary>
        public void RevealBodyOrbit(string bodyName)
        {
            AdvanceBodyToStage(bodyName, 3);
        }

        /// <summary>
        /// Legacy: advances body to stage 6 (full surface detail).
        /// </summary>
        public void RevealBodySurface(string bodyName)
        {
            AdvanceBodyToStage(bodyName, 6);
        }

        /// <summary>
        /// Advance body to the specified stage (0-6). Only advances forward —
        /// never reduces the stage. Updates DiscoveryInfo, the visual cache,
        /// and triggers a PCBM level update.
        /// </summary>
        public void AdvanceBodyToStage(string bodyName, int newStage)
        {
            if (newStage < 0) newStage = 0;
            if (newStage > 6) newStage = 6;
            int current = GetDiscoveryStage(bodyName);
            if (current >= newStage) return;
            SetBodyStage(bodyName, newStage);
            Debug.Log("[KASA] Body " + bodyName + " advanced to stage " + newStage);
        }

        private void SetBodyStage(string bodyName, int stage)
        {
            BodyDiscovered[bodyName] = stage;
            // Clear the cached level so SetBodyVisualLevel will actually apply the change
            _lastAppliedLevel.Remove(bodyName);
            CelestialBody body = FlightGlobals.Bodies.Find(b => b.bodyName == bodyName);
            if (body != null)
            {
                if (body.DiscoveryInfo != null)
                    body.DiscoveryInfo.SetLevel(GetTargetLevel(bodyName));
                SetBodyVisualLevel(body, DiscoveryStageToVisualLevel(stage));
            }
        }


        /// <summary>
        /// Convenience: set all moons of a parent to stage 1 when the parent SOI is entered.
        /// Moons exist but are unknown objects until their own SOI is entered.
        /// </summary>
        public void DiscoverMoonsOf(string parentBodyName)
        {
            CelestialBody parent = FlightGlobals.Bodies.Find(b => b.bodyName == parentBodyName);
            if (parent == null) return;
            foreach (CelestialBody moon in parent.orbitingBodies)
                DiscoverBody(moon.bodyName);
        }

        // body name -> KASA resources sourced there (built from KASA_RESOURCE nodes)
        private static Dictionary<string, List<string>> bodyResources;

        private static void BuildResourceMap()
        {
            bodyResources = new Dictionary<string, List<string>>();
            foreach (UrlDir.UrlConfig cfg in GameDatabase.Instance.GetConfigs("RESOURCE_DEFINITION"))
            {
                ConfigNode kasa = cfg.config.GetNode("KASA_RESOURCE");
                if (kasa == null) continue;
                string res = cfg.config.GetValue("name");
                string body = kasa.GetValue("body");
                if (string.IsNullOrEmpty(res) || string.IsNullOrEmpty(body)) continue;
                if (!bodyResources.ContainsKey(body)) bodyResources[body] = new List<string>();
                bodyResources[body].Add(res);
            }
        }

        public void UnlockResourcesForBody(string bodyName)
        {
            if (string.IsNullOrEmpty(bodyName) || PartUpgradeManager.Handler == null) return;
            if (bodyResources == null) BuildResourceMap();
            List<string> list;
            if (!bodyResources.TryGetValue(bodyName, out list)) return;
            foreach (string res in list)
            {
                string upg = "KASA_" + res;
                if (PartUpgradeManager.Handler.GetUpgrade(upg) == null) continue;
                if (PartUpgradeManager.Handler.IsUnlocked(upg)) continue;
                PartUpgradeManager.Handler.SetUnlocked(upg, true);
                PartUpgradeManager.Handler.SetEnabled(upg, true);
                Debug.Log("[KASA] Resource part options unlocked: " + res + " (" + bodyName + " scanned)");
            }
        }
    }

    // ================================================================
    // ADDON
    // Runs at MainMenu level (persists across scenes). Handles new
    // game creation and config loading.
    // ================================================================
    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public class KASADiscoveryAddon : MonoBehaviour
    {
        public static KASADiscoveryAddon Instance { get; private set; }

        // Bodies known at the start of a new career.
        // Populated from KASA_Discovery.cfg on load.
        public static List<string> KnownBodiesAtStart = new List<string>();

        // Has the config been read successfully?
        public static bool ConfigLoaded = false;

        void Awake()
        {
            if (Instance != null)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            LoadConfig();
            Debug.Log("[KASA] KASADiscoveryAddon initialised. " +
                      KnownBodiesAtStart.Count + " bodies known at career start.");

            GameEvents.onLevelWasLoaded.Add(OnSceneLoaded);
            GameEvents.OnMapEntered.Add(OnMapEntered);
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            GameEvents.onLevelWasLoaded.Remove(OnSceneLoaded);
            GameEvents.OnMapEntered.Remove(OnMapEntered);
        }

        private System.Collections.IEnumerator Start()
        {
            yield return null;
            yield return null;
            HideLaunchSiteMarkers();
        }

        private void OnSceneLoaded(GameScenes scene)
        {
            Debug.Log("[KASA] OnSceneLoaded: " + scene);
            if (KASADiscoveryScenario.Instance != null)
            {
                // Safety net for the SCANsat texture-disposal bug: drop the
                // last-applied cache so this scene re-applies every body's visual
                // level from scratch, restoring any texture disposed in the last scene.
                KASADiscoveryScenario.Instance.InvalidateVisualLevelCache();

                var go = KASADiscoveryScenario.Instance.gameObject;

                if (scene == GameScenes.FLIGHT)
                {
                    if (go.GetComponent<KASABodyHider>() == null)
                        go.AddComponent<KASABodyHider>();
                    if (go.GetComponent<KASASentinelUpdater>() == null)
                        go.AddComponent<KASASentinelUpdater>();
                }
                if (scene == GameScenes.TRACKSTATION || scene == GameScenes.SPACECENTER)
                {
                    if (go.GetComponent<KASASentinelUpdater>() == null)
                        go.AddComponent<KASASentinelUpdater>();
                }

                if (scene == GameScenes.FLIGHT)
                    StartCoroutine(HideLaunchSiteMarkersDelayed());
            }

            var s = KASADiscoveryScenario.Instance;
            if (s != null)
                foreach (var kvp in s.BodyResourceScanned)
                    if (kvp.Value) s.UnlockResourcesForBody(kvp.Key);

            // Tracking station is already in map view on load so OnMapEntered
            // never fires — call it directly, same as ResearchBodies does.
            if (scene == GameScenes.TRACKSTATION)
                OnMapEntered();
        }

        private void OnMapEntered()
        {
            Debug.Log("[KASA] OnMapEntered fired.");
            StartCoroutine(HideLaunchSiteMarkersDelayed());
        }

        private System.Collections.IEnumerator HideLaunchSiteMarkersDelayed()
        {
            // Wait a few frames for siteNodes to be populated
            yield return null;
            yield return null;
            yield return null;
            HideLaunchSiteMarkers();
        }

        private void HideLaunchSiteMarkers()
        {
            if (MapView.fetch == null || MapView.fetch.siteNodes == null) return;

            int hidden = 0;
            foreach (var node in MapView.fetch.siteNodes)
            {
                if (node == null || node.siteObject == null) continue;
                string name = node.siteObject.GetName() ?? "";
                string displayName = PSystemSetup.Instance != null
                    ? (PSystemSetup.Instance.GetLaunchSiteDisplayName(name) ?? name)
                    : name;
                if (!IsSiteToHide(name) && !IsSiteToHide(displayName)) continue;
                if (node.wayPoint != null && node.wayPoint.visible)
                {
                    node.wayPoint.visible = false;
                    node.wayPoint.enableMarker = false;
                    node.wayPoint.CleanupMapNode();
                    node.enabled = false;
                    //try { node.wayPoint.CleanupMapNode(); } catch { }
                    hidden++;
                    Debug.Log("[KASA] Hidden site: " + name);
                }
            }
            if (hidden > 0)
                Debug.Log("[KASA] Suppressed " + hidden + " site(s).");
        }

        private static readonly string[] SitesToHide = {
            "IslandAirfield", "Island Airfield",
            "Woomerang Launch Site", "Woomerang",
            "Dessert Airfield", "Dessert Launch Site",
            "Desert Airfield", "Desert Launch Site",
            "Glacier Lake Launch Site", "Glacier Lake",
            "Cove Launch Site", "Crater Launch Site",
            "Mahi Mahi Launch Site", "Baikerbanur",
        };

        private static bool IsSiteToHide(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (string site in SitesToHide)
                if (string.Equals(name, site, System.StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        // ----------------------------------------------------------------
        // Load KASA_Discovery.cfg from GameDatabase
        // ----------------------------------------------------------------
        void LoadConfig()
        {
            KnownBodiesAtStart.Clear();
            KASADiscoveryScenario.MoonParents.Clear();
            KASADiscoveryScenario.PlanetSMA.Clear();

            ConfigNode[] nodes = GameDatabase.Instance.GetConfigNodes("KASA_DISCOVERY");
            if (nodes.Length == 0)
            {
                Debug.LogWarning("[KASA] KASA_Discovery.cfg not found! Using fallback: Kerbin, Sun only.");
                KnownBodiesAtStart.Add("Sun");
                KnownBodiesAtStart.Add("Kerbin");
                return;
            }

            ConfigNode cfg = nodes[0];

            // Known bodies at career start
            foreach (string val in cfg.GetValues("knownBody"))
                KnownBodiesAtStart.Add(val.Trim());

            // Planet SMA data for Sentinel weighting
            foreach (ConfigNode pn in cfg.GetNodes("PLANET_DATA"))
            {
                string name = pn.GetValue("name") ?? "";
                string smaStr = pn.GetValue("sma") ?? "0";
                double sma;
                if (!string.IsNullOrEmpty(name) && double.TryParse(smaStr, out sma))
                    KASADiscoveryScenario.PlanetSMA[name] = sma;
            }

            // Moon parent relationships
            foreach (ConfigNode mn in cfg.GetNodes("MOON_DATA"))
            {
                string moon = mn.GetValue("moon") ?? "";
                string parent = mn.GetValue("parent") ?? "";
                if (!string.IsNullOrEmpty(moon) && !string.IsNullOrEmpty(parent))
                    KASADiscoveryScenario.MoonParents[moon] = parent;
            }

            ConfigLoaded = true;
            Debug.Log("[KASA] Config loaded. Known: " + string.Join(", ", KnownBodiesAtStart) +
                      " | Planets: " + KASADiscoveryScenario.PlanetSMA.Count +
                      " | Moons: " + KASADiscoveryScenario.MoonParents.Count);
        }

    }

    // ================================================================
    // CONTRACT CONFIGURATOR BEHAVIOUR — KASAStartSentinelSurvey
    // ================================================================
    public class KASAStartSentinelSurveyFactory : BehaviourFactory
    {
        private string triggerParameter;
        public override bool Load(ConfigNode configNode)
        {
            bool valid = base.Load(configNode);
            valid &= ConfigNodeUtil.ParseValue<string>(
                configNode, "parameter", x => triggerParameter = x, this);
            return valid;
        }
        public override ContractBehaviour Generate(ConfiguredContract contract)
        {
            return new KASAStartSentinelSurveyBehaviour(triggerParameter);
        }
    }

    public class KASAStartSentinelSurveyBehaviour : ContractBehaviour
    {
        private string triggerParameter;
        public KASAStartSentinelSurveyBehaviour() { }
        public KASAStartSentinelSurveyBehaviour(string parameter) { this.triggerParameter = parameter; }
        protected override void OnParameterStateChange(ContractParameter param)
        {
            if (param.State != ParameterState.Complete || param.ID != triggerParameter) return;
            if (KASADiscoveryScenario.Instance == null) return;
            KASADiscoveryScenario.Instance.ActivateSentinel();
            Debug.Log("[KASA] Sentinel survey activated.");
        }
        protected override void OnLoad(ConfigNode node) { triggerParameter = node.GetValue("parameter") ?? ""; }
        protected override void OnSave(ConfigNode node) { node.AddValue("parameter", triggerParameter); }
    }

    // ================================================================
    // CONTRACT CONFIGURATOR BEHAVIOUR — KASAStartOuterSurvey
    // Activates the outer survey (direct imaging of exterior bodies) when
    // the Gazer-relocation contract's trigger parameter completes.
    // ================================================================
    public class KASAStartOuterSurveyFactory : BehaviourFactory
    {
        private string triggerParameter;
        public override bool Load(ConfigNode configNode)
        {
            bool valid = base.Load(configNode);
            valid &= ConfigNodeUtil.ParseValue<string>(
                configNode, "parameter", x => triggerParameter = x, this);
            return valid;
        }
        public override ContractBehaviour Generate(ConfiguredContract contract)
        {
            return new KASAStartOuterSurveyBehaviour(triggerParameter);
        }
    }

    public class KASAStartOuterSurveyBehaviour : ContractBehaviour
    {
        private string triggerParameter;
        public KASAStartOuterSurveyBehaviour() { }
        public KASAStartOuterSurveyBehaviour(string parameter) { this.triggerParameter = parameter; }
        protected override void OnParameterStateChange(ContractParameter param)
        {
            if (param.State != ParameterState.Complete || param.ID != triggerParameter) return;
            if (KASADiscoveryScenario.Instance == null) return;
            KASADiscoveryScenario.Instance.ActivateOuterSurvey();
            Debug.Log("[KASA] Outer survey activated.");
        }
        protected override void OnLoad(ConfigNode node) { triggerParameter = node.GetValue("parameter") ?? ""; }
        protected override void OnSave(ConfigNode node) { node.AddValue("parameter", triggerParameter); }
    }

    // ================================================================
    // CONTRACT CONFIGURATOR REQUIREMENT
    // Allows contracts to gate on whether a specific body has been
    // discovered by the player.
    //
    // Usage in .cfg files:
    //   REQUIREMENT
    //   {
    //       name = MinmusDiscovered
    //       type = KASABodyDiscovered
    //       body = Minmus
    //   }
    // ================================================================
    public class KASABodyDiscoveredRequirement : ContractRequirement
    {
        // The body name to check (from cfg: body = Minmus)
        protected string targetBodyName { get; set; }

        public override bool LoadFromConfig(ConfigNode configNode)
        {
            bool valid = base.LoadFromConfig(configNode);
            valid &= ConfigNodeUtil.ParseValue<string>(
                configNode, "body", x => targetBodyName = x, this);
            checkOnActiveContract = false;
            return valid;
        }

        public override void OnLoad(ConfigNode configNode)
        {
            targetBodyName = configNode.GetValue("body") ?? "";
        }

        public override void OnSave(ConfigNode configNode)
        {
            configNode.AddValue("body", targetBodyName);
        }

        protected override string RequirementText()
        {
            return targetBodyName + " must have been discovered";
        }

        public override bool RequirementMet(ConfiguredContract contract)
        {
            if (string.IsNullOrEmpty(targetBodyName)) return false;
            if (KASADiscoveryScenario.Instance == null)
            {
                // Scenario not loaded — don't block contracts
                Debug.LogWarning("[KASA] KASABodyDiscoveredRequirement: Scenario not loaded, returning true.");
                return true;
            }
            return KASADiscoveryScenario.Instance.IsDiscovered(targetBodyName);
        }
    }

    // ================================================================
    // CONTRACT CONFIGURATOR BEHAVIOUR — KASAAdvanceDiscovery
    // ================================================================
    // CC uses a factory pattern for custom behaviours. Two classes are
    // required: KASAAdvanceDiscoveryFactory (which CC discovers by
    // reflection — class name minus "Factory" = type name in .cfg)
    // and KASAAdvanceDiscoveryBehaviour (the runtime instance).
    //
    // Usage in .cfg:
    //   BEHAVIOUR
    //   {
    //       name      = RevealDunaOrbit
    //       type      = KASAAdvanceDiscovery
    //       body      = Duna
    //       stage     = orbital          // or: surface
    //       parameter = OrbitalSurvey   // parameter that triggers this
    //   }
    // ================================================================
    public class KASAAdvanceDiscoveryFactory : BehaviourFactory
    {
        private string targetBodyName;
        private string triggerParameter;
        private string stage;

        public override bool Load(ConfigNode configNode)
        {
            bool valid = base.Load(configNode);
            valid &= ConfigNodeUtil.ParseValue<string>(
                configNode, "body", x => targetBodyName = x, this);
            // parameter is optional — if omitted, behaviour fires on contract completion
            ConfigNodeUtil.ParseValue<string>(
                configNode, "parameter", x => triggerParameter = x, this, "");
            valid &= ConfigNodeUtil.ParseValue<string>(
                configNode, "stage", x => stage = x, this, "orbital");
            return valid;
        }

        public override ContractBehaviour Generate(ConfiguredContract contract)
        {
            return new KASAAdvanceDiscoveryBehaviour(targetBodyName, triggerParameter, stage);
        }
    }

    public class KASAAdvanceDiscoveryBehaviour : ContractBehaviour
    {
        private string targetBodyName;
        private string triggerParameter;
        private string stage;

        // Parameterless constructor required by CC for deserialisation
        public KASAAdvanceDiscoveryBehaviour() { }

        public KASAAdvanceDiscoveryBehaviour(string body, string parameter, string stage)
        {
            this.targetBodyName = body;
            this.triggerParameter = parameter;
            this.stage = stage;
        }

        protected override void OnParameterStateChange(ContractParameter param)
        {
            if (string.IsNullOrEmpty(triggerParameter)) return;
            if (param.State != ParameterState.Complete) return;
            if (param.ID != triggerParameter) return;
            AdvanceBody();
        }

        protected override void OnCompleted()
        {
            // Fire on contract completion when no specific parameter is set
            if (!string.IsNullOrEmpty(triggerParameter)) return;
            AdvanceBody();
        }

        private void AdvanceBody()
        {
            if (KASADiscoveryScenario.Instance == null)
            {
                Debug.LogWarning("[KASA] KASAAdvanceDiscoveryBehaviour: Scenario not loaded.");
                return;
            }

            // Stage can be a name or a number (0-6). Names map to specific stages:
            //   presence    = 1 (FF-04 — crew spots two objects in sky)
            //   telescope   = 3 (DP-01 — telescope identifies them)
            //   observation = 4 (DP-02 — detailed observation)
            //   altimetry   = 5 (KSP-01/03 altimetry scan)
            //   biome       = 6 (KSP-01/03 biome scan)
            //   orbital     = 3 (legacy — same as telescope)
            //   surface     = 6 (legacy — same as biome)
            int targetStage = -1;
            switch (stage)
            {
                case "presence": targetStage = 1; break;
                case "telescope": targetStage = 3; break;
                case "observation": targetStage = 4; break;
                case "altimetry": targetStage = 5; break;
                case "biome": targetStage = 6; break;
                case "orbital": targetStage = 3; break;
                case "surface": targetStage = 6; break;
                default:
                    // Try parsing as number for direct stage specification
                    int.TryParse(stage, out targetStage);
                    break;
            }

            if (targetStage < 0 || targetStage > 6)
            {
                Debug.LogWarning("[KASA] KASAAdvanceDiscoveryBehaviour: Unknown stage '" + stage + "' for " + targetBodyName);
                return;
            }

            KASADiscoveryScenario.Instance.AdvanceBodyToStage(targetBodyName, targetStage);

            Debug.Log("[KASA] KASAAdvanceDiscoveryBehaviour: " + targetBodyName +
                      " advanced to stage " + targetStage + " (" + stage + ")");
        }

        protected override void OnLoad(ConfigNode node)
        {
            targetBodyName = node.GetValue("body") ?? "";
            triggerParameter = node.GetValue("parameter") ?? "";
            stage = node.GetValue("stage") ?? "orbital";
        }

        protected override void OnSave(ConfigNode node)
        {
            node.AddValue("body", targetBodyName);
            node.AddValue("parameter", triggerParameter);
            node.AddValue("stage", stage);
        }
    }

    // ================================================================
    // CONTRACT CONFIGURATOR REQUIREMENT — KASABodyDiscoveryStage
    // ================================================================
    // Gates a contract on a body being at or above a specific
    // discovery stage. Use this to ensure crewed missions only
    // appear after the orbital survey has been completed.
    //
    // Usage in .cfg:
    //   REQUIREMENT
    //   {
    //       name     = DunaOrbitKnown
    //       type     = KASABodyDiscoveryStage
    //       body     = Duna
    //       minStage = 2     // 1=SOI, 2=orbital survey, 3=surface scan
    //   }
    // ================================================================
    public class KASABodyDiscoveryStageRequirement : ContractRequirement
    {
        protected string targetBodyName { get; set; }
        protected int minStage { get; set; }

        public override bool LoadFromConfig(ConfigNode configNode)
        {
            bool valid = base.LoadFromConfig(configNode);
            valid &= ConfigNodeUtil.ParseValue<string>(
                configNode, "body", x => targetBodyName = x, this);
            valid &= ConfigNodeUtil.ParseValue<int>(
                configNode, "minStage", x => minStage = x, this, 1);
            checkOnActiveContract = false;
            return valid;
        }

        public override void OnLoad(ConfigNode configNode)
        {
            targetBodyName = configNode.GetValue("body") ?? "";
            int parsed = 1;
            int.TryParse(configNode.GetValue("minStage") ?? "1", out parsed);
            minStage = parsed;
        }

        public override void OnSave(ConfigNode configNode)
        {
            configNode.AddValue("body", targetBodyName);
            configNode.AddValue("minStage", minStage);
        }

        protected override string RequirementText()
        {
            string[] labels = { "hidden", "detected", "orbit mapped", "surface scanned" };
            string stageLabel = (minStage >= 0 && minStage <= 3) ? labels[minStage] : minStage.ToString();
            return targetBodyName + " must be at discovery stage: " + stageLabel + " or better";
        }

        public override bool RequirementMet(ConfiguredContract contract)
        {
            if (string.IsNullOrEmpty(targetBodyName)) return false;
            if (KASADiscoveryScenario.Instance == null) return true;
            return KASADiscoveryScenario.Instance.GetDiscoveryStage(targetBodyName) >= minStage;
        }
    }

    // ================================================================
    // CONTRACT CONFIGURATOR BEHAVIOUR — KASAResourceScanned
    // ================================================================
    // Fires when a named contract parameter completes, marking a body's
    // resource scan as done. This gates the resource lifecycle contracts
    // (sample return, analysis, mining base) that follow.
    //
    // Usage in .cfg:
    //   BEHAVIOUR
    //   {
    //       name      = MarkKerbinResourceScanned
    //       type      = KASAResourceScanned
    //       body      = Kerbin
    //       parameter = ResourceCampaign
    //   }
    // ================================================================
    public class KASAResourceScannedFactory : BehaviourFactory
    {
        private string targetBodyName;
        private string triggerParameter;

        public override bool Load(ConfigNode configNode)
        {
            bool valid = base.Load(configNode);
            valid &= ConfigNodeUtil.ParseValue<string>(
                configNode, "body", x => targetBodyName = x, this);
            valid &= ConfigNodeUtil.ParseValue<string>(
                configNode, "parameter", x => triggerParameter = x, this);
            return valid;
        }

        public override ContractBehaviour Generate(ConfiguredContract contract)
        {
            return new KASAResourceScannedBehaviour(targetBodyName, triggerParameter);
        }
    }

    public class KASAResourceScannedBehaviour : ContractBehaviour
    {
        private string targetBodyName;
        private string triggerParameter;

        public KASAResourceScannedBehaviour() { }

        public KASAResourceScannedBehaviour(string body, string parameter)
        {
            this.targetBodyName = body;
            this.triggerParameter = parameter;
        }

        protected override void OnParameterStateChange(ContractParameter param)
        {
            if (param.State != ParameterState.Complete) return;
            if (param.ID != triggerParameter) return;
            if (KASADiscoveryScenario.Instance == null)
            {
                Debug.LogWarning("[KASA] KASAResourceScannedBehaviour: Scenario not loaded.");
                return;
            }
            KASADiscoveryScenario.Instance.MarkResourceScanned(targetBodyName);
        }

        protected override void OnLoad(ConfigNode node)
        {
            targetBodyName = node.GetValue("body") ?? "";
            triggerParameter = node.GetValue("parameter") ?? "";
        }

        protected override void OnSave(ConfigNode node)
        {
            node.AddValue("body", targetBodyName);
            node.AddValue("parameter", triggerParameter);
        }
    }

    // ================================================================
    // CONTRACT CONFIGURATOR REQUIREMENT — KASACoordinateCovered
    // ================================================================
    // Returns true if a specific lat/lon coordinate on a body has been
    // scanned by SCANsat with the specified scanner type. Used to gate
    // anomaly contracts on the player's scanner having passed over the
    // anomaly location, rather than a fixed percentage threshold.
    //
    // If SCANsat is not installed, returns true (fail-open) so the
    // contract isn't permanently locked for players without SCANsat.
    //
    // Usage in .cfg:
    //   REQUIREMENT
    //   {
    //       name      = PyramidsCoordScanned
    //       type      = KASACoordinateCovered
    //       body      = Kerbin
    //       latitude  = -6.49976
    //       longitude = -141.68024
    //       scanType  = Altimetry     // SCANtype enum name
    //   }
    //
    // scanType values: Altimetry, AltimetryLoRes, AltimetryHiRes,
    //                  Biome, ResourceLoRes, ResourceHiRes
    // ================================================================
    public class KASACoordinateCoveredRequirement : ContractRequirement
    {
        protected string targetBodyName { get; set; }
        protected double latitude { get; set; }
        protected double longitude { get; set; }
        protected string scanType { get; set; }

        // Cached SCANsat reflection references — resolved once on first use.
        private static System.Reflection.MethodInfo _isCoveredMethod;
        private static System.Type _scanTypeEnum;
        private static bool _scanSatResolved;

        public override bool LoadFromConfig(ConfigNode configNode)
        {
            bool valid = base.LoadFromConfig(configNode);
            valid &= ConfigNodeUtil.ParseValue<string>(
                configNode, "body", x => targetBodyName = x, this);
            valid &= ConfigNodeUtil.ParseValue<double>(
                configNode, "latitude", x => latitude = x, this);
            valid &= ConfigNodeUtil.ParseValue<double>(
                configNode, "longitude", x => longitude = x, this);
            valid &= ConfigNodeUtil.ParseValue<string>(
                configNode, "scanType", x => scanType = x, this, "Altimetry");
            checkOnActiveContract = true;
            return valid;
        }

        public override void OnLoad(ConfigNode configNode)
        {
            targetBodyName = configNode.GetValue("body") ?? "";
            scanType = configNode.GetValue("scanType") ?? "Altimetry";
            double.TryParse(configNode.GetValue("latitude") ?? "0", out double lat); latitude = lat;
            double.TryParse(configNode.GetValue("longitude") ?? "0", out double lon); longitude = lon;
        }

        public override void OnSave(ConfigNode configNode)
        {
            configNode.AddValue("body", targetBodyName);
            configNode.AddValue("latitude", latitude);
            configNode.AddValue("longitude", longitude);
            configNode.AddValue("scanType", scanType);
        }

        protected override string RequirementText()
        {
            return string.Format(
                "{0} must be scanned at ({1:F4}, {2:F4}) with {3} scanner",
                targetBodyName, latitude, longitude, scanType);
        }

        public override bool RequirementMet(ConfiguredContract contract)
        {
            if (!ResolveSCANsat()) return true; // SCANsat absent — fail open

            CelestialBody body = FlightGlobals.Bodies
                .Find(b => b.bodyName == targetBodyName);
            if (body == null) return false;

            try
            {
                object scanTypeValue = Enum.Parse(_scanTypeEnum, scanType);
                return (bool)_isCoveredMethod.Invoke(
                    null, new object[] { longitude, latitude, body, scanTypeValue });
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning("[KASA] KASACoordinateCoveredRequirement error: " + ex.Message);
                return true; // fail open on error
            }
        }

        // Resolve SCANsat API via reflection. Cached after first call.
        // Returns false if SCANsat is not installed.
        private static bool ResolveSCANsat()
        {
            if (_scanSatResolved) return _isCoveredMethod != null;
            _scanSatResolved = true;

            AssemblyLoader.LoadedAssembly asm = null;
            foreach (var a in AssemblyLoader.loadedAssemblies)
            {
                if (a.assembly.GetName().Name == "SCANsat") { asm = a; break; }
            }
            if (asm == null) return false;

            _scanTypeEnum = asm.assembly.GetType("SCANsat.SCANdata+SCANtype");
            if (_scanTypeEnum == null) return false;

            var utilType = asm.assembly.GetType("SCANsat.SCANutil");
            if (utilType == null)
                utilType = asm.assembly.GetType("SCANsat.SCANUtil");
            if (utilType == null) return false;

            _isCoveredMethod = utilType.GetMethod("isCovered",
                new System.Type[] {
                    typeof(double), typeof(double),
                    typeof(CelestialBody), _scanTypeEnum
                });

            return _isCoveredMethod != null;
        }
    }

    // ================================================================
    // CONTRACT CONFIGURATOR REQUIREMENT — KASAIsResourceScanned
    // ================================================================
    // Returns true if the specified body's resource scan has been
    // completed (i.e. the KASAResourceScanned behaviour has fired
    // for that body via the resource scan contract).
    //
    // Usage in .cfg:
    //   REQUIREMENT
    //   {
    //       name  = MunResourceScanned
    //       type  = IsResourceScanned
    //       body  = Mun
    //   }
    // ================================================================
    public class IsResourceScannedRequirement : ContractRequirement
    {
        protected string targetBodyName { get; set; }

        public override bool LoadFromConfig(ConfigNode configNode)
        {
            bool valid = base.LoadFromConfig(configNode);
            valid &= ConfigNodeUtil.ParseValue<string>(
                configNode, "body", x => targetBodyName = x, this);
            checkOnActiveContract = false;
            return valid;
        }

        public override void OnLoad(ConfigNode configNode)
        {
            targetBodyName = configNode.GetValue("body") ?? "";
        }

        public override void OnSave(ConfigNode configNode)
        {
            configNode.AddValue("body", targetBodyName);
        }

        protected override string RequirementText()
        {
            return targetBodyName + " resource scan must be complete";
        }

        public override bool RequirementMet(ConfiguredContract contract)
        {
            if (string.IsNullOrEmpty(targetBodyName)) return false;
            if (KASADiscoveryScenario.Instance == null) return true;
            return KASADiscoveryScenario.Instance.IsResourceScanned(targetBodyName);
        }
    }

    // ================================================================
    // KASA BODY HIDER
    // ================================================================
    // Child MonoBehaviour attached to KASADiscoveryScenario's GameObject
    // in the flight scene. LateUpdate runs every frame after KSP's own
    // Update, ensuring undiscovered bodies stay hidden/blurred.
    // Using AddComponent on the ScenarioModule's own GameObject is the
    // correct pattern (as used by ResearchBodies) — it ensures LateUpdate
    // runs in the active scene context.
    // ================================================================
    public class KASABodyHider : MonoBehaviour
    {
        private void LateUpdate()
        {
            var scenario = KASADiscoveryScenario.Instance;
            if (scenario == null || FlightGlobals.Bodies == null) return;

            foreach (CelestialBody body in FlightGlobals.Bodies)
            {
                if (body == null) continue;
                int stage = scenario.GetDiscoveryStage(body.bodyName);
                int targetLevel = KASADiscoveryScenario.DiscoveryStageToVisualLevel(stage);

                // Only suppress MapObject (name label) for stage 0 bodies.
                // PCBM handles the visual sphere — we just stop the label popping up.
                if (stage == 0)
                {
                    if (body.MapObject != null && body.MapObject.gameObject.activeSelf)
                        body.MapObject.gameObject.SetActive(false);
                    if (body.orbitDriver != null &&
                        body.orbitDriver.Renderer != null &&
                        body.orbitDriver.Renderer.enabled)
                        body.orbitDriver.Renderer.enabled = false;
                }
            }
        }
    }

    // ================================================================
    // KASA SENTINEL UPDATER
    // ================================================================
    // Child MonoBehaviour that checks Sentinel detection timers and
    // moon discovery timers. Runs in flight, tracking station, and
    // space centre scenes. Uses FixedUpdate so it runs on game time
    // rather than real time (respects time warp).
    // ================================================================
    public class KASASentinelUpdater : MonoBehaviour
    {
        private float _checkInterval = 60f; // check every 60 real seconds
        private float _nextCheck = 0f;

        private void Update()
        {
            if (Time.time < _nextCheck) return;
            _nextCheck = Time.time + _checkInterval;

            var scenario = KASADiscoveryScenario.Instance;
            if (scenario == null) return;
            scenario.UpdateSentinelTimers();
        }
    }

    // Hides KASA drill harvesters for resources whose home body hasn't been
    // resource-scanned. It ONLY hides — it never force-shows fields, which is
    // what corrupted the part-action window before.
    [KSPAddon(KSPAddon.Startup.EveryScene, false)]
    public class KASAResourceGate : MonoBehaviour
    {
        private static Dictionary<string, string> resourceBody;

        private static void BuildMap()
        {
            resourceBody = new Dictionary<string, string>();
            foreach (UrlDir.UrlConfig cfg in GameDatabase.Instance.GetConfigs("RESOURCE_DEFINITION"))
            {
                ConfigNode kasa = cfg.config.GetNode("KASA_RESOURCE");
                if (kasa == null) continue;
                string res = cfg.config.GetValue("name");
                string body = kasa.GetValue("body");
                if (!string.IsNullOrEmpty(res) && !string.IsNullOrEmpty(body))
                    resourceBody[res] = body;
            }
        }

        private static bool Discovered(string resource)
        {
            if (resourceBody == null) BuildMap();
            var scen = KASADiscoveryScenario.Instance;
            if (scen == null) return true;
            string body;
            if (!resourceBody.TryGetValue(resource, out body)) return true;
            return scen.IsResourceScanned(body);
        }

        void Start()
        {
            if (!HighLogic.LoadedSceneIsEditor && !HighLogic.LoadedSceneIsFlight) return;
            GateAll();
            GameEvents.onEditorPartEvent.Add(OnEditorPart);
            GameEvents.onVesselChange.Add(OnVessel);
        }

        void OnDestroy()
        {
            GameEvents.onEditorPartEvent.Remove(OnEditorPart);
            GameEvents.onVesselChange.Remove(OnVessel);
        }

        private void OnEditorPart(ConstructionEventType t, Part p) { if (p != null) GatePart(p); }
        private void OnVessel(Vessel v) { GateAll(); }

        private void GateAll()
        {
            if (HighLogic.LoadedSceneIsEditor && EditorLogic.fetch != null && EditorLogic.fetch.ship != null)
                foreach (Part p in EditorLogic.fetch.ship.Parts) GatePart(p);
            else if (FlightGlobals.ActiveVessel != null)
                foreach (Part p in FlightGlobals.ActiveVessel.Parts) GatePart(p);
        }

        private void GatePart(Part part)
        {
            if (resourceBody == null) BuildMap();
            foreach (ModuleResourceHarvester h in part.Modules.OfType<ModuleResourceHarvester>())
            {
                if (!resourceBody.ContainsKey(h.ResourceName)) continue;   // stock Ore etc. — leave
                if (Discovered(h.ResourceName)) continue;                   // discovered — leave as-is
                // undiscovered — hide only
                if (h.IsActivated) h.StopResourceConverter();
                foreach (BaseEvent e in h.Events) { e.guiActive = false; e.guiActiveEditor = false; e.active = false; }
                foreach (BaseField f in h.Fields) { f.guiActive = false; f.guiActiveEditor = false; }
            }
        }
    }

}