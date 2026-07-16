// ================================================================
// KASA Logistics — universal control window (Phase 2)
// ----------------------------------------------------------------
// Toolbar-launched window that manages every REGISTERED hub from one
// place: routes shown as bidirectional edges with Send/Request, live
// in-transit countdowns, per-resource standing orders, and the
// register-as-hub toggle. Pure view/controller over the scenario —
// no logistics logic lives here.
//
// COMPILE NOTES (can't be verified outside KSP):
//   * Needs a project reference to ClickThroughBlocker.dll, and the
//     `using ClickThroughFix;` below. If your CTB version names the
//     class/method differently, the only call to change is the one in
//     OnGUI (ClickThruBlocker.GUILayoutWindow).
//   * Icon is loaded from GameData-relative "KASA/Icons/logistics".
//     Change ICON_PATH if your icons live elsewhere.
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
            windowRect = ClickThruBlocker.GUILayoutWindow(winId, windowRect, DrawWindow, "KASA Logistics");
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

            // --- action parameters (shared by every Send/Request/Order button) ---
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

            // --- in transit ---
            var inflight = s.Dispatches.OrderBy(d => d.ArrivalUT).ToList();
            GUILayout.Label("In transit (" + inflight.Count + ")");
            if (inflight.Count == 0)
                GUILayout.Label("   nothing moving.");
            else
                foreach (var d in inflight)
                {
                    double left = d.ArrivalUT - now;
                    string eta = left > 0 ? "ETA " + KASALogisticsScenario.FormatTime(left) : "arriving…";
                    GUILayout.Label("   " + d.Resource + " x" + d.Amount.ToString("F0") + " -> " +
                                    KASALogisticsScenario.HubDisplayName(d.DestHubId) + "   " + eta);
                }

            GUILayout.Space(4);
            scroll = GUILayout.BeginScrollView(scroll);

            // --- registered hubs ---
            var hubs = KASALogisticsScenario.AllHubVessels();
            if (hubs.Count == 0)
                GUILayout.Label("No hubs registered yet. Register one below.");

            foreach (Vessel hv in hubs)
            {
                string hid = hv.id.ToString();
                GUILayout.BeginVertical(GUI.skin.box);

                GUILayout.BeginHorizontal();
                GUILayout.Label("<b>" + hv.vesselName + "</b>");
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Unregister", GUILayout.Width(90)))
                {
                    s.RegisteredHubs.Remove(hid);
                    Msg(hv.vesselName + " is no longer a hub.");
                }
                GUILayout.EndHorizontal();

                // routes touching this hub
                var routes = s.Routes.Values.Where(r => r.HubA == hid || r.HubB == hid).ToList();
                if (routes.Count == 0)
                    GUILayout.Label("   no routes.");
                foreach (var r in routes)
                {
                    string other = (r.HubA == hid) ? r.HubB : r.HubA;
                    string payload = string.Join(", ",
                        r.Payload.Select(kv => kv.Key + " " + kv.Value.ToString("F0")).ToArray());
                    double time = r.OneWay ? r.LegAB.Time : r.TotalTime;

                    GUILayout.Label("   " + (r.OneWay ? "-> " : "<-> ") +
                                    KASALogisticsScenario.HubDisplayName(other) +
                                    "  ·  " + (payload.Length > 0 ? payload : "empty") +
                                    "  ·  " + KASALogisticsScenario.FormatTime(time));

                    bool canSend = (r.HubA == hid) || (r.HubB == hid && !r.OneWay);
                    bool canReq  = (r.HubB == hid) || (r.HubA == hid && !r.OneWay);

                    GUILayout.BeginHorizontal();
                    GUILayout.Space(18);
                    GUI.enabled = canSend;
                    if (GUILayout.Button("Send " + SelRes + " ->", GUILayout.Width(150)))
                        DoDispatch(r, hid);
                    GUI.enabled = canReq;
                    if (GUILayout.Button("<- Request", GUILayout.Width(110)))
                        DoDispatch(r, other);
                    GUI.enabled = true;
                    if (GUILayout.Button("Order", GUILayout.Width(64)))
                        AddOrder(r, hid);
                    GUILayout.EndHorizontal();
                }

                // standing orders originating here
                var orders = s.Orders.Where(o => o.OriginHubId == hid).ToList();
                if (orders.Count > 0)
                {
                    GUILayout.Label("   Standing orders:");
                    foreach (var o in orders)
                    {
                        GUILayout.BeginHorizontal();
                        string status = string.IsNullOrEmpty(o.LastStall) ? "running" : "stalled: " + o.LastStall;
                        GUILayout.Label("      " + o.Resource + "  reserve " + o.Reserve.ToString("F0") +
                                        "  [" + status + "]");
                        GUILayout.FlexibleSpace();
                        if (GUILayout.Button("x", GUILayout.Width(24)))
                        {
                            s.Orders.Remove(o);
                            Msg("Standing order cancelled: " + o.Resource + " at " + hv.vesselName + ".");
                        }
                        GUILayout.EndHorizontal();
                    }
                }

                GUILayout.EndVertical();
            }

            // --- register new hubs ---
            var candidates = KASALogisticsScenario.AllHubMarkerVessels()
                .Where(v => !s.RegisteredHubs.Contains(v.id.ToString())).ToList();
            if (candidates.Count > 0)
            {
                GUILayout.Space(4);
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

            GUILayout.EndScrollView();
            GUILayout.EndVertical();
            GUI.DragWindow(new Rect(0, 0, 10000, 22));
        }

        // ---------------------------------------------------------------- actions
        void DoDispatch(KASARoute route, string fromHub)
        {
            double amt = Amount();
            if (amt <= 0) { Msg("Set a valid amount first."); return; }
            string reason;
            if (KASALogisticsScenario.Instance.Dispatch(route, fromHub, SelRes, amt, out reason))
                Msg(reason);          // Dispatch reports the run summary on success
            else
                Msg(reason);          // …and the stall reason on failure
        }

        void AddOrder(KASARoute route, string originHub)
        {
            var s = KASALogisticsScenario.Instance;
            if (s.Orders.Any(o => o.OriginHubId == originHub && o.Resource == SelRes))
            {
                Msg("A standing order for " + SelRes + " already exists here.");
                return;
            }
            double amt = Amount();
            var order = new KASAStandingOrder
            {
                RouteId = route.Id,
                OriginHubId = originHub,
                Resource = SelRes,
                Reserve = amt,                 // amount doubles as the origin reserve
                FillTarget = double.MaxValue   // ship until the destination is full
            };
            s.Orders.Add(order);
            Msg("Standing order: ship " + SelRes + " from " +
                KASALogisticsScenario.HubDisplayName(originHub) + ", keeping " + amt.ToString("F0") + " here.");
        }
    }
}
