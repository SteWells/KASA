// ================================================================
// KASA Logistics — universal control window
// ----------------------------------------------------------------
// Toolbar-launched, resizable window with two tabs:
//   Hubs   — register/unregister hubs.
//   Routes — every route once, with the Active toggle + live status,
//            one-shot Send/Reverse, and delete.
// Pure view/controller over KASALogisticsScenario.
//
// COMPILE NOTES (can't be verified outside KSP):
//   * Needs ClickThroughBlocker.dll referenced (using ClickThroughFix).
//     The only CTB call is in OnGUI (ClickThruBlocker.GUILayoutWindow).
//   * Icon loaded from GameData-relative ICON_PATH below.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using KSP.UI.Screens;      // ApplicationLauncher(Button)
using ClickThroughFix;      // ClickThroughBlocker

namespace KASA
{
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class KASALogisticsUI : MonoBehaviour
    {
        const string ICON_PATH = "KASA/Icons/logistics";

        static ApplicationLauncherButton button;
        bool show;
        Rect windowRect = new Rect(150, 120, 470, 540);
        Vector2 scroll = Vector2.zero;
        readonly int winId = "KASALogisticsUI".GetHashCode();

        int tab;                       // 0 Hubs, 1 Routes
        bool resizing;
        string pendingDelete = "";     // routeId awaiting a confirm click

        List<string> resList = new List<string>();
        int resIndex;
        string amountText = "1000";

        string SelRes { get { return (resList.Count > 0) ? resList[Mathf.Clamp(resIndex, 0, resList.Count - 1)] : ""; } }

        // ---------------------------------------------------------------- lifecycle
        void Start()
        {
            resList = KASALogisticsScenario.CargoResources.ToList();
            GameEvents.onGUIApplicationLauncherReady.Add(AddButton);
            GameEvents.onGUIApplicationLauncherDestroyed.Add(RemoveButton);
            AddButton();
        }

        void OnDestroy()
        {
            GameEvents.onGUIApplicationLauncherReady.Remove(AddButton);
            GameEvents.onGUIApplicationLauncherDestroyed.Remove(RemoveButton);
            RemoveButton();
        }

        void AddButton()
        {
            if (button != null || ApplicationLauncher.Instance == null) return;
            Texture2D tex = GameDatabase.Instance.GetTexture(ICON_PATH, false);
            button = ApplicationLauncher.Instance.AddModApplication(
                () => show = true,
                () => show = false,
                null, null, null, null,
                ApplicationLauncher.AppScenes.FLIGHT,
                tex);
        }

        void RemoveButton()
        {
            if (button == null || ApplicationLauncher.Instance == null) return;
            ApplicationLauncher.Instance.RemoveModApplication(button);
            button = null;
        }

        // ---------------------------------------------------------------- draw
        void OnGUI()
        {
            if (!show || !HighLogic.LoadedSceneIsFlight) return;
            if (KASALogisticsScenario.Instance == null) return;
            GUI.skin = HighLogic.Skin;
            // Fixed to windowRect's size; the resize grip owns width/height, so we only
            // take the dragged position back from the returned rect.
            Rect r = ClickThruBlocker.GUILayoutWindow(winId, windowRect, DrawWindow, "KASA Logistics",
                GUILayout.Width(windowRect.width), GUILayout.Height(windowRect.height));
            windowRect.x = r.x;
            windowRect.y = r.y;
        }

        static void Msg(string s)
        {
            ScreenMessages.PostScreenMessage("[KASA] " + s, 8f, ScreenMessageStyle.UPPER_CENTER);
        }

        double Amount()
        {
            double a;
            return double.TryParse(amountText, out a) && a > 0 ? a : 0;
        }

        void DrawWindow(int id)
        {
            var s = KASALogisticsScenario.Instance;
            double now = Planetarium.GetUniversalTime();

            GUILayout.BeginVertical();

            int newTab = GUILayout.Toolbar(tab, new[] { "Hubs", "Routes" });
            if (newTab != tab) { tab = newTab; pendingDelete = ""; }
            GUILayout.Space(2);

            scroll = GUILayout.BeginScrollView(scroll, GUILayout.ExpandHeight(true));
            if (tab == 0) DrawHubsTab(s);
            else DrawRoutesTab(s, now);
            GUILayout.EndScrollView();

            GUILayout.EndVertical();

            // resize grip (bottom-right) — handled before DragWindow so it consumes its clicks
            Rect grip = new Rect(windowRect.width - 20, windowRect.height - 20, 18, 18);
            GUI.Box(grip, "\u25E2");
            HandleResize(grip);

            GUI.DragWindow();   // drag from any empty area; controls capture their own clicks
        }

        void HandleResize(Rect grip)
        {
            Event e = Event.current;
            if (e.type == EventType.MouseDown && grip.Contains(e.mousePosition)) { resizing = true; e.Use(); }
            else if (e.type == EventType.MouseUp && resizing) { resizing = false; }
            else if (e.type == EventType.MouseDrag && resizing)
            {
                windowRect.width = Mathf.Max(380f, windowRect.width + e.delta.x);
                windowRect.height = Mathf.Max(300f, windowRect.height + e.delta.y);
                e.Use();
            }
        }

        // ---------------------------------------------------------------- Hubs tab
        void DrawHubsTab(KASALogisticsScenario s)
        {
            var hubs = KASALogisticsScenario.AllHubVessels();
            GUILayout.Label("Registered hubs (" + hubs.Count + ")");
            if (hubs.Count == 0)
                GUILayout.Label("   none — register one below.");
            foreach (Vessel hv in hubs)
            {
                GUILayout.BeginHorizontal(GUI.skin.box);
                GUILayout.Label(hv.vesselName);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Unregister", GUILayout.Width(90)))
                {
                    s.RegisteredHubs.Remove(hv.id.ToString());
                    Msg(hv.vesselName + " is no longer a hub.");
                }
                GUILayout.EndHorizontal();
            }

            var candidates = KASALogisticsScenario.AllHubMarkerVessels()
                .Where(v => !s.RegisteredHubs.Contains(v.id.ToString())).ToList();
            if (candidates.Count > 0)
            {
                GUILayout.Space(6);
                GUILayout.Label("Register a hub:");
                foreach (Vessel cv in candidates)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("   " + cv.vesselName);
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Register", GUILayout.Width(90)))
                    {
                        s.RegisteredHubs.Add(cv.id.ToString());
                        Msg(cv.vesselName + " registered as a hub.");
                    }
                    GUILayout.EndHorizontal();
                }
            }
        }

        // ---------------------------------------------------------------- Routes tab
        void DrawRoutesTab(KASALogisticsScenario s, double now)
        {
            // one-shot action parameters
            GUILayout.BeginHorizontal(GUI.skin.box);
            GUILayout.Label("Cargo:", GUILayout.Width(48));
            if (GUILayout.Button("<", GUILayout.Width(24)) && resList.Count > 0)
                resIndex = (resIndex - 1 + resList.Count) % resList.Count;
            GUILayout.Label(SelRes, GUILayout.Width(110));
            if (GUILayout.Button(">", GUILayout.Width(24)) && resList.Count > 0)
                resIndex = (resIndex + 1) % resList.Count;
            GUILayout.FlexibleSpace();
            GUILayout.Label("Amount:", GUILayout.Width(58));
            amountText = GUILayout.TextField(amountText ?? "", GUILayout.Width(80));
            GUILayout.EndHorizontal();

            // in transit
            var inflight = s.Dispatches.OrderBy(d => d.ArrivalUT).ToList();
            GUILayout.Label("In transit (" + inflight.Count + ")");
            foreach (var d in inflight)
            {
                double left = d.ArrivalUT - now;
                string eta = left > 0 ? "ETA " + KASALogisticsScenario.FormatTime(left) : "arriving…";
                GUILayout.Label("   " + d.Resource + " x" + d.Amount.ToString("F0") + " -> " +
                                KASALogisticsScenario.HubDisplayName(d.DestHubId) + "   " + eta);
            }

            GUILayout.Space(4);
            GUILayout.Label("Routes (" + s.Routes.Count + ")");
            if (s.Routes.Count == 0)
                GUILayout.Label("   none recorded yet.");

            foreach (var r in s.Routes.Values.ToList())
            {
                GUILayout.BeginVertical(GUI.skin.box);

                string payload = string.Join(", ",
                    r.Payload.Select(kv => kv.Key + " " + kv.Value.ToString("F0")).ToArray());
                double time = r.OneWay ? r.LegAB.Time : r.TotalTime;
                GUILayout.Label("<b>" + KASALogisticsScenario.HubDisplayName(r.Source) + " -> " +
                                KASALogisticsScenario.HubDisplayName(r.Dest) + "</b>" +
                                (r.OneWay ? "  (one-way)" : ""));
                GUILayout.Label("   " + (payload.Length > 0 ? payload : "empty") +
                                "  ·  " + KASALogisticsScenario.FormatTime(time));

                // Active toggle + live status (round trips only)
                if (!r.OneWay)
                {
                    GUILayout.BeginHorizontal();
                    bool nowActive = GUILayout.Toggle(r.Active, r.Active ? " Active" : " Activate",
                                                      GUILayout.Width(90));
                    if (nowActive != r.Active)
                    {
                        r.Active = nowActive;
                        if (!nowActive) r.LastStatus = "";   // staged cargo stays put
                    }
                    if (r.Active)
                        GUILayout.Label("  " + (string.IsNullOrEmpty(r.LastStatus) ? "starting…" : r.LastStatus));
                    GUILayout.EndHorizontal();
                }

                // one-shot manual dispatch (disabled while active) + delete
                GUILayout.BeginHorizontal();
                GUI.enabled = !r.Active;
                if (GUILayout.Button("Send " + SelRes + " ->", GUILayout.Width(150)))
                    DoDispatch(r, r.Source);
                GUI.enabled = !r.Active && !r.OneWay;
                if (GUILayout.Button("<- Reverse", GUILayout.Width(100)))
                    DoDispatch(r, r.Dest);
                GUI.enabled = true;
                GUILayout.FlexibleSpace();
                if (pendingDelete == r.Id)
                {
                    if (GUILayout.Button("Confirm", GUILayout.Width(80)))
                    {
                        s.DeleteRoute(r.Id);
                        Msg("Route deleted.");
                        pendingDelete = "";
                    }
                    if (GUILayout.Button("x", GUILayout.Width(24)))
                        pendingDelete = "";
                }
                else if (GUILayout.Button("Delete", GUILayout.Width(70)))
                {
                    pendingDelete = r.Id;
                }
                GUILayout.EndHorizontal();

                GUILayout.EndVertical();
            }
        }

        // ---------------------------------------------------------------- action
        void DoDispatch(KASARoute route, string fromHub)
        {
            double amt = Amount();
            if (amt <= 0) { Msg("Set a valid amount first."); return; }
            string reason;
            KASALogisticsScenario.Instance.Dispatch(route, fromHub, SelRes, amt, out reason);
            Msg(reason);   // Dispatch reports the run summary or the failure reason
        }
    }
}