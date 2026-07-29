// ================================================================
// KASA — PART HIDING SPIKE  (THROWAWAY — DELETE AFTER TESTING)
// ----------------------------------------------------------------
// Pairs with KASA_HideSpike.cfg. Answers the DESIGN-003 question:
// can a part be truly hidden, then revealed at runtime?
//
// CONTROLS: a small window appears in the VAB/SPH and at the Space
// Centre with two buttons:
//   [Toggle hidden/visible]   flip the spike part
//   [Log state]               dump its state to the KSP log
//
// (Buttons rather than keybinds so this needs no extra assembly
//  references — UnityEngine.InputLegacyModule.dll is NOT required.)
//
// WHAT TO CHECK (this is the actual experiment):
//   1. Fresh load, go to the VAB. The spike part must NOT appear in
//      any category.
//   2. TYPE "kasaspike" IN THE VAB SEARCH BOX. This is the important
//      one — some "hidden" parts still surface via search. If it
//      appears here, it is not truly hidden.
//   3. Open R&D. It must not appear under the Start node.
//   4. Click Toggle in the VAB. Does it appear WITHOUT leaving the
//      scene? (If it only appears after a scene change, the reveal
//      works but needs a refresh — note which.)
//   5. Click Toggle again. Does it disappear again?
//   6. Place one, save the craft, Toggle to hide, reload the craft.
//      Does the saved craft still load with the part? (This decides
//      whether hiding is save-safe.)
//
// Report back: which of 1-6 behaved, and the [Log state] output.
// ================================================================

using System;
using UnityEngine;
using KSP.UI.Screens;
using ClickThroughFix;   // already referenced by KASALogisticsUI

namespace KASA
{
    [KSPAddon(KSPAddon.Startup.EveryScene, false)]
    public class KASAPartHideSpike : MonoBehaviour
    {
        const string SPIKE_PART = "kasaSpikeTank";

        // What the part should look like when visible.
        const PartCategories VISIBLE_CATEGORY = PartCategories.FuelTank;

        Rect winRect = new Rect(60, 60, 260, 110);
        readonly int winId = "KASAPartHideSpike".GetHashCode();

        void OnGUI()
        {
            // Only where the test matters: the editor and the Space Centre (R&D).
            if (!HighLogic.LoadedSceneIsEditor &&
                HighLogic.LoadedScene != GameScenes.SPACECENTER) return;

            GUI.skin = HighLogic.Skin;
            winRect = ClickThruBlocker.GUILayoutWindow(winId, winRect, DrawWindow, "KASA hide spike");
        }

        void DrawWindow(int id)
        {
            AvailablePart ap = PartLoader.getPartInfoByName(SPIKE_PART);
            GUILayout.BeginVertical();

            if (ap == null)
            {
                GUILayout.Label("part not found — is KASA_HideSpike.cfg installed?");
            }
            else
            {
                GUILayout.Label(ap.TechHidden ? "currently HIDDEN" : "currently VISIBLE");
                if (GUILayout.Button("Toggle hidden/visible")) Toggle();
                if (GUILayout.Button("Log state")) LogState();
            }

            GUILayout.EndVertical();
            GUI.DragWindow();
        }

        static AvailablePart Spike()
        {
            AvailablePart ap = PartLoader.getPartInfoByName(SPIKE_PART);
            if (ap == null)
                Debug.Log("[KASA-SPIKE] part '" + SPIKE_PART + "' NOT FOUND — is KASA_HideSpike.cfg installed?");
            return ap;
        }

        void Toggle()
        {
            AvailablePart ap = Spike();
            if (ap == null) return;

            bool nowHidden = ap.TechHidden;
            SetVisible(ap, nowHidden);            // if hidden -> make visible
            Debug.Log("[KASA-SPIKE] toggled -> " + (nowHidden ? "VISIBLE" : "HIDDEN"));
            ScreenMessages.PostScreenMessage(
                "[KASA-SPIKE] part is now " + (nowHidden ? "VISIBLE" : "HIDDEN") +
                " — check the parts list AND the search box",
                6f, ScreenMessageStyle.UPPER_CENTER);

            LogState();
        }

        /// <summary>The actual reveal/hide mechanism under test. This is the code
        /// that would eventually be driven by resource discovery instead of a key.</summary>
        public static void SetVisible(AvailablePart ap, bool visible)
        {
            if (ap == null) return;

            ap.TechHidden = !visible;
            ap.category   = visible ? VISIBLE_CATEGORY : PartCategories.none;

            // Make sure the part is actually purchased/available when shown, so a
            // "visible but unusable" result is not mistaken for a hiding failure.
            if (visible)
            {
                try
                {
                    if (ResearchAndDevelopment.Instance != null &&
                        !ResearchAndDevelopment.PartModelPurchased(ap))
                    {
                        ResearchAndDevelopment.AddExperimentalPart(ap);
                        Debug.Log("[KASA-SPIKE] part was not purchased; added as experimental");
                    }
                }
                catch (Exception e) { Debug.Log("[KASA-SPIKE] purchase check failed: " + e.Message); }
            }
            else
            {
                try
                {
                    if (ResearchAndDevelopment.Instance != null)
                        ResearchAndDevelopment.RemoveExperimentalPart(ap);
                }
                catch (Exception e) { Debug.Log("[KASA-SPIKE] remove-experimental failed: " + e.Message); }
            }

            RefreshEditor();
        }

        /// <summary>Ask the editor to rebuild its parts list. If this does not work,
        /// the reveal still succeeds but needs a scene change — worth knowing which.</summary>
        static void RefreshEditor()
        {
            if (!HighLogic.LoadedSceneIsEditor) return;
            try
            {
                if (EditorPartList.Instance != null)
                {
                    EditorPartList.Instance.Refresh();
                    Debug.Log("[KASA-SPIKE] EditorPartList.Refresh() called");
                }
                else Debug.Log("[KASA-SPIKE] EditorPartList.Instance was null");
            }
            catch (Exception e)
            {
                // Non-fatal: tells us the refresh API is the blocker, not the hiding.
                Debug.Log("[KASA-SPIKE] EditorPartList.Refresh() threw: " + e);
            }
        }

        void LogState()
        {
            AvailablePart ap = Spike();
            if (ap == null) return;

            bool purchased = false;
            try { purchased = ResearchAndDevelopment.PartModelPurchased(ap); }
            catch (Exception e) { Debug.Log("[KASA-SPIKE] PartModelPurchased threw: " + e.Message); }

            Debug.Log("[KASA-SPIKE] ---- state ----" +
                      "\n  scene        = " + HighLogic.LoadedScene +
                      "\n  name         = " + ap.name +
                      "\n  title        = " + ap.title +
                      "\n  TechHidden   = " + ap.TechHidden +
                      "\n  category     = " + ap.category +
                      "\n  TechRequired = " + ap.TechRequired +
                      "\n  purchased    = " + purchased);
        }
    }
}
