// ================================================================
// KASA — PART GATE  (DESIGN-003 step 1)
// ----------------------------------------------------------------
// Hides parts until the fuel they belong to has been earned, and
// reveals them when it is.
//
// Proven by the hiding spike: setting TechHidden = true and
// category = none hides a part completely — invisible in the tech
// tree AND the VAB, including both search boxes. Flipping them back
// makes the part behave normally. Hiding HIDES, it does not disable:
// a craft already containing the part still loads and flies.
//
// WHAT DRIVES IT
//   KASADiscoveryScenario.UnlockedFuels  (persisted per save)
//     set by the KASAFuelUnlocked contract behaviour below, on
//     completion of the crewed sample-return contract for that
//     fuel's source body.
//
// WHAT IT READS
//   KASA_PART_GATE nodes, from anywhere in GameData. See
//   KASA_GatedParts.cfg. One node per (fuel, category) pair:
//
//     KASA_PART_GATE
//     {
//         fuel     = PrismaticGel
//         category = Propulsion       // category to restore when revealed
//         part     = kasa_gel_terrier
//         part     = kasa_gel_poodle
//     }
//
// NOTE ON PURCHASING
//   Revealing only makes a part VISIBLE. It still has to be bought in
//   R&D like any other part, which is the intended flow. Every gated
//   part therefore needs a real TechRequired node.
//
// COMPILE NOTES (not verifiable outside KSP)
//   * EditorPartList.Instance.Refresh() is wrapped — if it throws, the
//     reveal still works, it just needs a scene change to show up.
//     Harmless in practice: unlocks happen in flight.
// ================================================================

using System;
using System.Collections.Generic;
using UnityEngine;
using KSP.UI.Screens;         // EditorPartList
using Contracts;              // ContractParameter
using ContractConfigurator;   // ContractBehaviour, BehaviourFactory, ConfiguredContract

namespace KASA
{
    // ================================================================
    // THE GATE
    // ================================================================
    [KSPAddon(KSPAddon.Startup.EveryScene, false)]
    public class KASAPartGate : MonoBehaviour
    {
        private class GateEntry
        {
            public string PartName;
            public PartCategories Category;
        }

        // fuel -> parts gated behind it
        private static Dictionary<string, List<GateEntry>> fuelParts;

        // ------------------------------------------------------------
        // Config
        // ------------------------------------------------------------
        private static void BuildMap()
        {
            fuelParts = new Dictionary<string, List<GateEntry>>();
            if (GameDatabase.Instance == null) return;

            foreach (UrlDir.UrlConfig cfg in GameDatabase.Instance.GetConfigs("KASA_PART_GATE"))
            {
                string fuel = cfg.config.GetValue("fuel");
                if (string.IsNullOrEmpty(fuel))
                {
                    Debug.LogWarning("[KASA] KASA_PART_GATE node with no 'fuel' — skipped.");
                    continue;
                }

                PartCategories cat = PartCategories.Propulsion;
                string catRaw = cfg.config.GetValue("category");
                if (!string.IsNullOrEmpty(catRaw))
                {
                    try { cat = (PartCategories)Enum.Parse(typeof(PartCategories), catRaw, true); }
                    catch
                    {
                        Debug.LogWarning("[KASA] KASA_PART_GATE (" + fuel + "): unknown category '" +
                                         catRaw + "' — defaulting to Propulsion.");
                    }
                }

                List<GateEntry> list;
                if (!fuelParts.TryGetValue(fuel, out list))
                {
                    list = new List<GateEntry>();
                    fuelParts[fuel] = list;
                }

                foreach (string partName in cfg.config.GetValues("part"))
                {
                    if (string.IsNullOrEmpty(partName)) continue;
                    list.Add(new GateEntry { PartName = partName, Category = cat });
                }
            }

            int total = 0;
            foreach (var kv in fuelParts) total += kv.Value.Count;
            Debug.Log("[KASA] Part gate: " + fuelParts.Count + " fuel(s), " + total + " gated part(s).");
        }

        // ------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------
        void Start()
        {
            // Apply wherever the player could see a part list.
            if (!HighLogic.LoadedSceneIsEditor &&
                !HighLogic.LoadedSceneIsFlight &&
                HighLogic.LoadedScene != GameScenes.SPACECENTER) return;

            ApplyAll();
        }

        // ------------------------------------------------------------
        // Applying state
        // ------------------------------------------------------------

        /// <summary>Set every gated part to match the current save's unlock state.</summary>
        public static void ApplyAll()
        {
            if (fuelParts == null) BuildMap();

            KASADiscoveryScenario scen = KASADiscoveryScenario.Instance;
            if (scen == null)
            {
                // No career scenario (main menu, or a non-career game): leave parts
                // exactly as their configs declare them. Do NOT hide or reveal.
                return;
            }

            foreach (var kv in fuelParts)
            {
                bool unlocked = scen.IsFuelUnlocked(kv.Key);
                foreach (GateEntry e in kv.Value) SetVisible(e, unlocked);
            }

            RefreshEditor();
        }

        /// <summary>Reveal one fuel's parts. Called by MarkFuelUnlocked.</summary>
        public static void Reveal(string fuel)
        {
            if (string.IsNullOrEmpty(fuel)) return;
            if (fuelParts == null) BuildMap();

            List<GateEntry> list;
            if (!fuelParts.TryGetValue(fuel, out list))
            {
                Debug.LogWarning("[KASA] Part gate: no KASA_PART_GATE entries for fuel '" + fuel +
                                 "' — nothing to reveal.");
                return;
            }

            foreach (GateEntry e in list) SetVisible(e, true);
            Debug.Log("[KASA] Part gate: revealed " + list.Count + " part(s) for " + fuel + ".");
            RefreshEditor();
        }

        /// <summary>The actual mechanism, as proven by the spike.</summary>
        private static void SetVisible(GateEntry e, bool visible)
        {
            AvailablePart ap = PartLoader.getPartInfoByName(e.PartName);
            if (ap == null)
            {
                Debug.LogWarning("[KASA] Part gate: part '" + e.PartName + "' not found.");
                return;
            }

            ap.TechHidden = !visible;
            ap.category = visible ? e.Category : PartCategories.none;
        }

        /// <summary>Rebuild the editor parts list, if we happen to be in the editor.
        /// Non-fatal: unlocks happen in flight, so a scene change does it anyway.</summary>
        private static void RefreshEditor()
        {
            if (!HighLogic.LoadedSceneIsEditor) return;
            try
            {
                if (EditorPartList.Instance != null) EditorPartList.Instance.Refresh();
            }
            catch (Exception ex)
            {
                Debug.Log("[KASA] Part gate: editor refresh skipped (" + ex.Message + ").");
            }
        }
    }


    // ================================================================
    // CONTRACT CONFIGURATOR BEHAVIOUR — KASAFuelUnlocked
    // ----------------------------------------------------------------
    // Reveals a fuel's parts when the contract COMPLETES. Unlike
    // KASAResourceScanned (which fires on a named parameter), this
    // waits for the whole contract, because the gate is "crew landed,
    // sample returned" — i.e. the contract's entire point.
    //
    // Usage in .cfg:
    //   BEHAVIOUR
    //   {
    //       name = UnlockPrismaticGel
    //       type = KASAFuelUnlocked
    //       fuel = PrismaticGel
    //   }
    // ================================================================
    public class KASAFuelUnlockedFactory : BehaviourFactory
    {
        private string fuelName;

        public override bool Load(ConfigNode configNode)
        {
            bool valid = base.Load(configNode);
            valid &= ConfigNodeUtil.ParseValue<string>(
                configNode, "fuel", x => fuelName = x, this);
            return valid;
        }

        public override ContractBehaviour Generate(ConfiguredContract contract)
        {
            return new KASAFuelUnlockedBehaviour(fuelName);
        }
    }

    public class KASAFuelUnlockedBehaviour : ContractBehaviour
    {
        private string fuelName;

        public KASAFuelUnlockedBehaviour() { }

        public KASAFuelUnlockedBehaviour(string fuel)
        {
            this.fuelName = fuel;
        }

        protected override void OnCompleted()
        {
            if (KASADiscoveryScenario.Instance == null)
            {
                Debug.LogWarning("[KASA] KASAFuelUnlockedBehaviour: Scenario not loaded.");
                return;
            }
            // MarkFuelUnlocked is idempotent, so a re-fire is harmless.
            KASADiscoveryScenario.Instance.MarkFuelUnlocked(fuelName);
        }

        protected override void OnLoad(ConfigNode node)
        {
            fuelName = node.GetValue("fuel") ?? "";
        }

        protected override void OnSave(ConfigNode node)
        {
            node.AddValue("fuel", fuelName);
        }
    }
}