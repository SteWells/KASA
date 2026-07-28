// ================================================================
// KASA — Logistics System
// KASALogistics.cs   (rewrite — implements LOGISTICS_DESIGN.md)
// ================================================================
// THE MODEL, in one breath:
//   Hubs are nodes. Routes are measured edges. All storage is real tanks,
//   except KSC's, which is funds. Within 1 km hubs pool freely, provided
//   each has crew. Beyond that you fly it once and we measure it. End the
//   recording where you started for a reusable ROUND TRIP; end it at the
//   far hub for an expendable ONE-WAY. Standing orders cycle the edge and
//   stall honestly when fuel, funds or storage run out.
//
// THERE IS NO DEPOT. The "pool" is a VIEW over real tanks. A 1,000-unit
// base holds 1,000 units. Fill it and mining stops, exactly as stock does.
//
// Local and long-haul are the SAME mechanism with different numbers. The
// data model does not know what a vehicle is, nor how far the edge reaches.
// A rover crossing 40km of Mun and a freighter crossing to Dres are the
// same object: one just has a long time and a tiny fuel bill.
//
// ---------------------------------------------------------------
// WHAT IS NOT HERE (deliberately)
//   Background production. Stock drills/converters only tick while their
//   vessel is LOADED. Until the duty-cycle certification + catch-up sim
//   (step 4) is written, an unattended base produces NOTHING, so an active
//   route will drain the source's buffer and then sit on "waiting for cargo"
//   until you return to the base. This is expected, not a bug.
//
// ---------------------------------------------------------------
// FLAGGED FOR VERIFICATION (assumptions not testable outside KSP)
//   [V1] Vessel cost snapshot: AvailablePart.cost is assumed to INCLUDE a
//        full load of resources, so dry = ap.cost - sum(maxAmount*unitCost).
//        Check the log line "[KASA] one-way cost snapshot" against the VAB.
//   [V2] onPartCouple Guid migration. Docking merges vessels and destroys
//        one Guid. A manual "Reassign Hauler" PAW button is the escape hatch.
//   [V3] Funding.Instance is null in Sandbox. All funds paths are guarded.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace KASA
{
    // ================================================================
    // A leg of a route: fuel consumed + time taken, one direction.
    // ================================================================
    public class KASALeg
    {
        public Dictionary<string, double> Fuel = new Dictionary<string, double>();
        public double Time = 0;

        public double FuelMass
        {
            get
            {
                double m = 0;
                foreach (var kv in Fuel)
                {
                    PartResourceDefinition def = PartResourceLibrary.Instance.GetDefinition(kv.Key);
                    m += kv.Value * (def != null ? def.density : 0.001);
                }
                return m;
            }
        }

        public void Save(ConfigNode n)
        {
            n.AddValue("time", Time);
            ConfigNode f = n.AddNode("FUEL");
            foreach (var kv in Fuel) f.AddValue(kv.Key, kv.Value);
        }

        public static KASALeg Load(ConfigNode n)
        {
            KASALeg l = new KASALeg();
            double d;
            if (double.TryParse(n.GetValue("time"), out d)) l.Time = d;
            ConfigNode f = n.GetNode("FUEL");
            if (f != null)
                foreach (ConfigNode.Value v in f.values)
                    if (double.TryParse(v.value, out d)) l.Fuel[v.name] = d;
            return l;
        }
    }


    // ================================================================
    // ROUTE — a measured edge between two specific hubs.
    // Keyed by HUB PAIR, not by body. Two bases on the Mun therefore have
    // two independent routes to the same station.
    // ================================================================
    public class KASARoute
    {
        public string HubA = "";      // hub the recording STARTED at
        public string HubB = "";      // the far hub
        public string BodyName = "";  // display only

        public KASALeg LegAB = new KASALeg();   // A -> B
        public KASALeg LegBA = new KASALeg();   // B -> A  (empty for one-way)

        public Dictionary<string, double> Payload = new Dictionary<string, double>();

        public bool OneWay = false;
        public double VesselCost = 0;    // one-way only: full editor cost, snapshotted
        public string HaulerId = "";   // round-trip only. Guid, never a name.
        public string HaulerName = "";   // display only
        public double RecordedUT = 0;

        // --- active-route automation (round trips only) ---
        public bool Active = false;
        public bool WaitForFull = true;                 // false = ship whatever is loaded
        public string SourceHubId = "";                 // cargo source; empty falls back to HubA
        public int Phase = 0;                            // 0 Idle, 1 Staged (loaded, holding), 2 InFlight
        public Dictionary<string, double> Staged = new Dictionary<string, double>(); // held cargo
        public string LastStatus = "";                  // live status for the window

        public string Id { get { return HubA + ">" + HubB; } }
        public string Source { get { return string.IsNullOrEmpty(SourceHubId) ? HubA : SourceHubId; } }
        public string Dest { get { return Source == HubA ? HubB : HubA; } }

        public double TotalTime { get { return LegAB.Time + LegBA.Time; } }
        public double FuelMass { get { return LegAB.FuelMass + LegBA.FuelMass; } }

        public double Capacity
        {
            get { double t = 0; foreach (var kv in Payload) t += kv.Value; return t; }
        }

        /// <summary>Round-trip fuel bill. Empty for one-way (cost is funds instead).</summary>
        public Dictionary<string, double> RoundTripFuel()
        {
            var r = new Dictionary<string, double>();
            if (OneWay) return r;
            foreach (var leg in new[] { LegAB, LegBA })
                foreach (var kv in leg.Fuel)
                {
                    if (!r.ContainsKey(kv.Key)) r[kv.Key] = 0;
                    r[kv.Key] += kv.Value;
                }
            return r;
        }

        /// <summary>Used to pick the best route and to compare recordings.</summary>
        public double CostPerUnit
        {
            get
            {
                double cap = Math.Max(Capacity, 1.0);
                return OneWay ? VesselCost / cap : FuelMass / cap;
            }
        }

        /// <summary>Travel time from 'from' to the other end.</summary>
        public double TimeFrom(string from)
        {
            if (OneWay) return LegAB.Time;
            return (from == HubA) ? LegAB.Time : LegBA.Time;
        }

        public void Save(ConfigNode n)
        {
            n.AddValue("hubA", HubA);
            n.AddValue("hubB", HubB);
            n.AddValue("bodyName", BodyName);
            n.AddValue("oneWay", OneWay);
            n.AddValue("vesselCost", VesselCost);
            n.AddValue("haulerId", HaulerId);
            n.AddValue("haulerName", HaulerName);
            n.AddValue("recordedUT", RecordedUT);
            n.AddValue("active", Active);
            n.AddValue("waitForFull", WaitForFull);
            n.AddValue("sourceHubId", SourceHubId);
            n.AddValue("phase", Phase);
            { ConfigNode st = n.AddNode("STAGED"); foreach (var kv in Staged) st.AddValue(kv.Key, kv.Value); }
            LegAB.Save(n.AddNode("LEG_AB"));
            LegBA.Save(n.AddNode("LEG_BA"));
            ConfigNode p = n.AddNode("PAYLOAD");
            foreach (var kv in Payload) p.AddValue(kv.Key, kv.Value);
        }

        public static KASARoute Load(ConfigNode n)
        {
            KASARoute r = new KASARoute();
            r.HubA = n.GetValue("hubA") ?? "";
            r.HubB = n.GetValue("hubB") ?? "";
            r.BodyName = n.GetValue("bodyName") ?? "";
            r.HaulerId = n.GetValue("haulerId") ?? "";
            r.HaulerName = n.GetValue("haulerName") ?? "";
            bool b; double d;
            if (bool.TryParse(n.GetValue("oneWay"), out b)) r.OneWay = b;
            if (double.TryParse(n.GetValue("vesselCost"), out d)) r.VesselCost = d;
            if (double.TryParse(n.GetValue("recordedUT"), out d)) r.RecordedUT = d;
            if (bool.TryParse(n.GetValue("active"), out b)) r.Active = b;
            if (bool.TryParse(n.GetValue("waitForFull"), out b)) r.WaitForFull = b; else r.WaitForFull = true;
            r.SourceHubId = n.GetValue("sourceHubId") ?? "";
            { int ph; if (int.TryParse(n.GetValue("phase"), out ph)) r.Phase = ph; }
            { ConfigNode st = n.GetNode("STAGED"); if (st != null) foreach (ConfigNode.Value cv in st.values) { double sv; if (double.TryParse(cv.value, out sv)) r.Staged[cv.name] = sv; } }
            if (n.GetNode("LEG_AB") != null) r.LegAB = KASALeg.Load(n.GetNode("LEG_AB"));
            if (n.GetNode("LEG_BA") != null) r.LegBA = KASALeg.Load(n.GetNode("LEG_BA"));
            ConfigNode p = n.GetNode("PAYLOAD");
            if (p != null)
                foreach (ConfigNode.Value v in p.values)
                    if (double.TryParse(v.value, out d)) r.Payload[v.name] = d;
            return r;
        }
    }


    // ================================================================
    // ACTIVE RECORDING — persisted, keyed by hauler Guid so that MULTIPLE
    // routes may be recorded concurrently. This is the NORMAL case: a Dres
    // route takes years of game time, and you will fly other missions.
    // ================================================================
    public class KASAActiveRecording
    {
        public string HaulerId = "";
        public string HaulerName = "";
        public string StartHubId = "";
        public string BodyName = "";
        public double StartUT = 0;

        public bool MidpointDone = false;
        public string FarHubId = "";
        public double MidpointUT = 0;
        public string SourceHubId = "";   // hub the player LOADED at = cargo source

        public Dictionary<string, double> Leg1 = new Dictionary<string, double>();
        public Dictionary<string, double> Leg2 = new Dictionary<string, double>();
        public Dictionary<string, double> Peak = new Dictionary<string, double>();
        public Dictionary<string, double> Sample = new Dictionary<string, double>();
        public Dictionary<string, double> Loaded = new Dictionary<string, double>(); // payload = what was loaded at the source

        public void Save(ConfigNode n)
        {
            n.AddValue("haulerId", HaulerId);
            n.AddValue("haulerName", HaulerName);
            n.AddValue("startHubId", StartHubId);
            n.AddValue("bodyName", BodyName);
            n.AddValue("startUT", StartUT);
            n.AddValue("midpointDone", MidpointDone);
            n.AddValue("farHubId", FarHubId);
            n.AddValue("midpointUT", MidpointUT);
            n.AddValue("sourceHubId", SourceHubId);
            SaveD(n, "LEG1", Leg1); SaveD(n, "LEG2", Leg2);
            SaveD(n, "PEAK", Peak); SaveD(n, "SAMPLE", Sample);
            SaveD(n, "LOADED", Loaded);
        }

        public static KASAActiveRecording Load(ConfigNode n)
        {
            KASAActiveRecording r = new KASAActiveRecording();
            r.HaulerId = n.GetValue("haulerId") ?? "";
            r.HaulerName = n.GetValue("haulerName") ?? "";
            r.StartHubId = n.GetValue("startHubId") ?? "";
            r.BodyName = n.GetValue("bodyName") ?? "";
            r.FarHubId = n.GetValue("farHubId") ?? "";
            r.SourceHubId = n.GetValue("sourceHubId") ?? "";
            double d; bool b;
            if (double.TryParse(n.GetValue("startUT"), out d)) r.StartUT = d;
            if (double.TryParse(n.GetValue("midpointUT"), out d)) r.MidpointUT = d;
            if (bool.TryParse(n.GetValue("midpointDone"), out b)) r.MidpointDone = b;
            r.Leg1 = LoadD(n, "LEG1"); r.Leg2 = LoadD(n, "LEG2");
            r.Peak = LoadD(n, "PEAK"); r.Sample = LoadD(n, "SAMPLE");
            r.Loaded = LoadD(n, "LOADED");
            return r;
        }

        private static void SaveD(ConfigNode p, string name, Dictionary<string, double> d)
        {
            ConfigNode n = p.AddNode(name);
            foreach (var kv in d) n.AddValue(kv.Key, kv.Value);
        }

        private static Dictionary<string, double> LoadD(ConfigNode p, string name)
        {
            var d = new Dictionary<string, double>();
            ConfigNode n = p.GetNode(name);
            if (n == null) return d;
            double v;
            foreach (ConfigNode.Value cv in n.values)
                if (double.TryParse(cv.value, out v)) d[cv.name] = v;
            return d;
        }
    }


    // ================================================================
    // DISPATCH — cargo in transit on an edge.
    // ================================================================
    public class KASADispatch
    {
        public string RouteId = "";
        public string DestHubId = "";
        public string Resource = "";
        public double Amount = 0;
        public double ArrivalUT = 0;

        public void Save(ConfigNode n)
        {
            n.AddValue("routeId", RouteId);
            n.AddValue("destHubId", DestHubId);
            n.AddValue("resource", Resource);
            n.AddValue("amount", Amount);
            n.AddValue("arrivalUT", ArrivalUT);
        }

        public static KASADispatch Load(ConfigNode n)
        {
            KASADispatch d = new KASADispatch();
            d.RouteId = n.GetValue("routeId") ?? "";
            d.DestHubId = n.GetValue("destHubId") ?? "";
            d.Resource = n.GetValue("resource") ?? "";
            double v;
            if (double.TryParse(n.GetValue("amount"), out v)) d.Amount = v;
            if (double.TryParse(n.GetValue("arrivalUT"), out v)) d.ArrivalUT = v;
            return d;
        }
    }


    // ================================================================
    // STANDING ORDER — cycles an edge until a condition stalls it.
    // ================================================================
    // ================================================================
    // SCENARIO
    // ================================================================
    [KSPScenario(
        ScenarioCreationOptions.AddToNewCareerGames |
        ScenarioCreationOptions.AddToExistingCareerGames,
        GameScenes.SPACECENTER, GameScenes.FLIGHT, GameScenes.TRACKSTATION)]
    public class KASALogisticsScenario : ScenarioModule
    {
        public static KASALogisticsScenario Instance { get; private set; }

        public const string KSC_HUB = "KSC";
        public const double POOL_RANGE = 1000.0;   // metres — free local pooling
        private const double SAMPLE_INTERVAL = 0.5;
        private const double ROUTE_INTERVAL = 2.0;

        public Dictionary<string, KASARoute> Routes = new Dictionary<string, KASARoute>();
        public Dictionary<string, KASAActiveRecording> Recordings = new Dictionary<string, KASAActiveRecording>();
        public List<KASADispatch> Dispatches = new List<KASADispatch>();

        /// <summary>haulerId -> UT it is free again. This is what makes throughput finite.</summary>
        public Dictionary<string, double> HaulerBusyUntil = new Dictionary<string, double>();

        /// <summary>Vessel Guids the player has opted in as hubs. The marker rides on every
        /// command part, so registration — not the module — decides what acts as a hub.
        /// KSC is always a hub and is never listed here.</summary>
        public HashSet<string> RegisteredHubs = new HashSet<string>();

        private double lastSampleUT = 0;
        private double lastRouteUT = 0;

        public static readonly HashSet<string> CargoResources = new HashSet<string>
        {
            "Regocite", "Glassmonite", "Ferrosite", "Kerium", "Evonite",
            "Moherium", "Laythite", "Elysium", "Consumables",
            "PrismaticGel", "DenseOxidiser", "ThermicMix", "Aetherium", "Ore",
            "LiquidFuel", "Oxidizer"
        };

        private static readonly HashSet<string> IgnoredForFuel = new HashSet<string>
        {
            "ElectricCharge", "Ablator", "SolidFuel"
        };

        public static bool IsCargo(string r) { return CargoResources.Contains(r); }

        // Dual-use resources are BOTH cargo and propellant. They count as cargo only in a
        // KASA holding tank (KASACargoTank) and as fuel only in a normal feed tank, so a
        // route can haul them without a tanker burning its own load or draining a station's
        // maneuvering fuel. Everything else ignores the distinction.
        public static readonly HashSet<string> DualUse = new HashSet<string> { "LiquidFuel", "Oxidizer" };
        public static bool IsDualUse(string r) { return DualUse.Contains(r); }
        public static bool IsPureCargo(string r) { return CargoResources.Contains(r) && !DualUse.Contains(r); }

        static bool PartIsCargoTank(Part p)
        {
            return p != null && p.Modules != null && p.Modules.Contains("KASACargoTank");
        }
        static bool ProtoIsCargoTank(ProtoPartSnapshot pps)
        {
            if (pps == null) return false;
            foreach (ProtoPartModuleSnapshot m in pps.modules) if (m.moduleName == "KASACargoTank") return true;
            return false;
        }

        public override void OnAwake()
        {
            base.OnAwake();
            Instance = this;
            GameEvents.onPartCouple.Add(OnPartCouple);
        }

        private void OnDestroy()
        {
            GameEvents.onPartCouple.Remove(OnPartCouple);
            if (Instance == this) Instance = null;
        }

        // ----------------------------------------------------------------
        // [V2] Docking merges two vessels: one Guid dies. Migrate any
        // recording / hauler binding onto the survivor.
        // ----------------------------------------------------------------
        private void OnPartCouple(GameEvents.FromToAction<Part, Part> e)
        {
            if (e.from == null || e.to == null) return;
            if (e.from.vessel == null || e.to.vessel == null) return;

            string dying = e.from.vessel.id.ToString();
            string survivor = e.to.vessel.id.ToString();
            if (dying == survivor) return;
            MigrateVessel(dying, survivor);
        }

        public void MigrateVessel(string oldId, string newId)
        {
            KASAActiveRecording rec;
            if (Recordings.TryGetValue(oldId, out rec))
            {
                Recordings.Remove(oldId);
                rec.HaulerId = newId;
                Recordings[newId] = rec;
                Debug.Log("[KASA] Logistics: migrated recording " + oldId + " -> " + newId);
            }

            foreach (var r in Routes.Values)
                if (r.HaulerId == oldId) r.HaulerId = newId;

            double until;
            if (HaulerBusyUntil.TryGetValue(oldId, out until))
            {
                HaulerBusyUntil.Remove(oldId);
                HaulerBusyUntil[newId] = until;
            }

            if (RegisteredHubs.Remove(oldId)) RegisteredHubs.Add(newId);
        }

        // ----------------------------------------------------------------
        // Persistence
        // ----------------------------------------------------------------
        public override void OnSave(ConfigNode node)
        {
            base.OnSave(node);
            foreach (var r in Routes.Values) r.Save(node.AddNode("ROUTE"));
            foreach (var r in Recordings.Values) r.Save(node.AddNode("RECORDING"));
            foreach (var d in Dispatches) d.Save(node.AddNode("DISPATCH"));
            foreach (var kv in HaulerBusyUntil)
            {
                ConfigNode b = node.AddNode("BUSY");
                b.AddValue("haulerId", kv.Key);
                b.AddValue("until", kv.Value);
            }
            foreach (string id in RegisteredHubs) node.AddValue("registeredHub", id);
        }

        public override void OnLoad(ConfigNode node)
        {
            base.OnLoad(node);
            Routes.Clear(); Recordings.Clear(); Dispatches.Clear();
            HaulerBusyUntil.Clear();

            foreach (ConfigNode n in node.GetNodes("ROUTE"))
            {
                KASARoute r = KASARoute.Load(n);
                if (!string.IsNullOrEmpty(r.HubA)) Routes[r.Id] = r;
            }
            foreach (ConfigNode n in node.GetNodes("RECORDING"))
            {
                KASAActiveRecording r = KASAActiveRecording.Load(n);
                if (!string.IsNullOrEmpty(r.HaulerId)) Recordings[r.HaulerId] = r;
            }
            foreach (ConfigNode n in node.GetNodes("DISPATCH")) Dispatches.Add(KASADispatch.Load(n));
            foreach (ConfigNode n in node.GetNodes("BUSY"))
            {
                double u;
                string id = n.GetValue("haulerId");
                if (!string.IsNullOrEmpty(id) && double.TryParse(n.GetValue("until"), out u))
                    HaulerBusyUntil[id] = u;
            }

            RegisteredHubs.Clear();
            foreach (string id in node.GetValues("registeredHub"))
                if (!string.IsNullOrEmpty(id) && !IsKSC(id)) RegisteredHubs.Add(id);

            // Upgrade path: a save made before the registry existed has routes but no
            // registered hubs. Auto-register every endpoint of an existing route so the
            // current network keeps working without the player re-registering by hand.
            if (RegisteredHubs.Count == 0 && Routes.Count > 0)
            {
                foreach (var r in Routes.Values)
                {
                    if (!string.IsNullOrEmpty(r.HubA) && !IsKSC(r.HubA)) RegisteredHubs.Add(r.HubA);
                    if (!string.IsNullOrEmpty(r.HubB) && !IsKSC(r.HubB)) RegisteredHubs.Add(r.HubB);
                }
                Debug.Log("[KASA] Logistics: auto-registered " + RegisteredHubs.Count +
                          " hub(s) from existing routes.");
            }

            Debug.Log("[KASA] Logistics: " + Routes.Count + " routes, " + Recordings.Count +
                      " recordings, " + Dispatches.Count + " in transit.");
        }

        // ================================================================
        // HUB PRIMITIVES
        // ================================================================
        public static bool IsKSC(string hubId) { return hubId == KSC_HUB; }

        public static Vessel VesselById(string id)
        {
            if (string.IsNullOrEmpty(id) || id == KSC_HUB) return null;
            return FlightGlobals.Vessels.FirstOrDefault(v => v.id.ToString() == id);
        }

        public static string HubDisplayName(string hubId)
        {
            if (IsKSC(hubId)) return "KSC";
            Vessel v = VesselById(hubId);
            return v != null ? v.vesselName : "(missing vessel)";
        }

        /// <summary>World position, valid loaded or unloaded.</summary>
        public static Vector3d HubPosition(Vessel v)
        {
            if (v == null) return Vector3d.zero;
            if (v.loaded) return v.GetWorldPos3D();
            if (v.LandedOrSplashed && v.mainBody != null)
                return v.mainBody.GetWorldSurfacePosition(v.latitude, v.longitude, v.altitude);
            return v.GetWorldPos3D();
        }

        /// <summary>Stock KSC coordinates on the home body.</summary>
        public static Vector3d KSCPosition()
        {
            CelestialBody home = FlightGlobals.GetHomeBody();
            if (home == null) return Vector3d.zero;
            return home.GetWorldSurfacePosition(-0.0972, -74.5577, 70.0);
        }

        public static int CrewCount(Vessel v)
        {
            if (v == null) return 0;
            if (v.loaded) return v.GetCrewCount();
            if (v.protoVessel != null) return v.protoVessel.GetVesselCrew().Count;
            return 0;
        }

        /// <summary>Every vessel that CARRIES the hub marker (loaded or not) — i.e. every
        /// candidate a player could register. Registration, not the marker, decides which
        /// of these actually act as hubs (see AllHubVessels).</summary>
        public static List<Vessel> AllHubMarkerVessels()
        {
            var list = new List<Vessel>();
            foreach (Vessel v in FlightGlobals.Vessels)
            {
                if (v == null) continue;
                if (v.loaded)
                {
                    if (v.FindPartModulesImplementing<KASALogisticsHub>().Count > 0) list.Add(v);
                }
                else if (v.protoVessel != null)
                {
                    bool found = false;
                    foreach (ProtoPartSnapshot pps in v.protoVessel.protoPartSnapshots)
                    {
                        foreach (ProtoPartModuleSnapshot m in pps.modules)
                            if (m.moduleName == "KASALogisticsHub") { found = true; break; }
                        if (found) break;
                    }
                    if (found) list.Add(v);
                }
            }
            return list;
        }

        /// <summary>Is this hub id an active hub? KSC always is; a vessel must be registered.</summary>
        public static bool IsRegisteredHub(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            if (IsKSC(id)) return true;
            return Instance != null && Instance.RegisteredHubs.Contains(id);
        }

        /// <summary>Every hub vessel that is ACTIVE — carries the marker AND is registered.
        /// This is what pooling, HubBeside, and dispatch all see.</summary>
        public static List<Vessel> AllHubVessels()
        {
            var list = new List<Vessel>();
            if (Instance == null) return list;
            foreach (Vessel v in AllHubMarkerVessels())
                if (Instance.RegisteredHubs.Contains(v.id.ToString())) list.Add(v);
            return list;
        }

        /// <summary>
        /// A hub's local pool: every CREWED hub vessel within POOL_RANGE on the same
        /// body, including itself. Crew is required at every hub in a transfer —
        /// someone has to connect the hoses. The hauler itself need not be crewed.
        /// </summary>
        public static List<Vessel> LocalPool(Vessel origin, Vessel exclude = null)
        {
            var pool = new List<Vessel>();
            if (origin == null) return pool;
            Vector3d p0 = HubPosition(origin);

            foreach (Vessel v in AllHubVessels())
            {
                if (v == exclude) continue;                     // never let a hauler pool from itself
                if (v.mainBody != origin.mainBody) continue;
                if (CrewCount(v) < 1) continue;
                if (Vector3d.Distance(HubPosition(v), p0) > POOL_RANGE) continue;
                pool.Add(v);
            }
            return pool;
        }

        public static bool NearKSC(Vessel v)
        {
            if (v == null || v.mainBody != FlightGlobals.GetHomeBody()) return false;
            return Vector3d.Distance(HubPosition(v), KSCPosition()) <= POOL_RANGE;
        }

        // ================================================================
        // RESOURCE ACCESS (loaded and unloaded)
        // ================================================================
        public static double VesselAmount(Vessel v, string res, bool cargoIntent = true)
        {
            double t = 0;
            if (v == null) return 0;
            bool dual = IsDualUse(res);
            if (v.loaded)
            {
                foreach (Part p in v.parts)
                {
                    if (dual && PartIsCargoTank(p) != cargoIntent) continue;
                    foreach (PartResource r in p.Resources)
                        if (r.resourceName == res) t += r.amount;
                }
            }
            else if (v.protoVessel != null)
            {
                foreach (ProtoPartSnapshot pps in v.protoVessel.protoPartSnapshots)
                {
                    if (dual && ProtoIsCargoTank(pps) != cargoIntent) continue;
                    foreach (ProtoPartResourceSnapshot prs in pps.resources)
                        if (prs.resourceName == res) t += prs.amount;
                }
            }
            return t;
        }

        public static double VesselSpace(Vessel v, string res, bool cargoIntent = true)
        {
            double t = 0;
            if (v == null) return 0;
            bool dual = IsDualUse(res);
            if (v.loaded)
            {
                foreach (Part p in v.parts)
                {
                    if (dual && PartIsCargoTank(p) != cargoIntent) continue;
                    foreach (PartResource r in p.Resources)
                        if (r.resourceName == res) t += (r.maxAmount - r.amount);
                }
            }
            else if (v.protoVessel != null)
            {
                foreach (ProtoPartSnapshot pps in v.protoVessel.protoPartSnapshots)
                {
                    if (dual && ProtoIsCargoTank(pps) != cargoIntent) continue;
                    foreach (ProtoPartResourceSnapshot prs in pps.resources)
                        if (prs.resourceName == res) t += (prs.maxAmount - prs.amount);
                }
            }
            return t;
        }

        public static double AddToVessel(Vessel v, string res, double amount, bool cargoIntent = true)
        {
            double remaining = amount;
            if (v == null || amount <= 0) return 0;
            bool dual = IsDualUse(res);

            if (v.loaded)
            {
                foreach (Part p in v.parts)
                {
                    if (dual && PartIsCargoTank(p) != cargoIntent) continue;
                    foreach (PartResource r in p.Resources)
                    {
                        if (r.resourceName != res) continue;
                        double space = r.maxAmount - r.amount;
                        if (space <= 0) continue;
                        double add = Math.Min(space, remaining);
                        r.amount += add; remaining -= add;
                        if (remaining <= 0) return amount;
                    }
                }
            }
            else if (v.protoVessel != null)
            {
                foreach (ProtoPartSnapshot pps in v.protoVessel.protoPartSnapshots)
                {
                    if (dual && ProtoIsCargoTank(pps) != cargoIntent) continue;
                    foreach (ProtoPartResourceSnapshot prs in pps.resources)
                    {
                        if (prs.resourceName != res) continue;
                        double space = prs.maxAmount - prs.amount;
                        if (space <= 0) continue;
                        double add = Math.Min(space, remaining);
                        prs.amount += add; remaining -= add;
                        if (remaining <= 0) return amount;
                    }
                }
            }
            return amount - remaining;
        }

        public static double TakeFromVessel(Vessel v, string res, double amount, bool cargoIntent = true)
        {
            double remaining = amount;
            if (v == null || amount <= 0) return 0;
            bool dual = IsDualUse(res);

            if (v.loaded)
            {
                foreach (Part p in v.parts)
                {
                    if (dual && PartIsCargoTank(p) != cargoIntent) continue;
                    foreach (PartResource r in p.Resources)
                    {
                        if (r.resourceName != res || r.amount <= 0) continue;
                        double take = Math.Min(r.amount, remaining);
                        r.amount -= take; remaining -= take;
                        if (remaining <= 0) return amount;
                    }
                }
            }
            else if (v.protoVessel != null)
            {
                foreach (ProtoPartSnapshot pps in v.protoVessel.protoPartSnapshots)
                {
                    if (dual && ProtoIsCargoTank(pps) != cargoIntent) continue;
                    foreach (ProtoPartResourceSnapshot prs in pps.resources)
                    {
                        if (prs.resourceName != res || prs.amount <= 0) continue;
                        double take = Math.Min(prs.amount, remaining);
                        prs.amount -= take; remaining -= take;
                        if (remaining <= 0) return amount;
                    }
                }
            }
            return amount - remaining;
        }

        // ---- Pool-level access. KSC has infinite storage, backed by funds. ----

        public static double PoolAmount(string hubId, string res, Vessel exclude = null, bool cargoIntent = true)
        {
            if (IsKSC(hubId)) return double.MaxValue;
            Vessel v = VesselById(hubId);
            if (v == null) return 0;
            double t = 0;
            foreach (Vessel h in LocalPool(v, exclude)) t += VesselAmount(h, res, cargoIntent);
            return t;
        }

        public static double PoolSpace(string hubId, string res, Vessel exclude = null, bool cargoIntent = true)
        {
            if (IsKSC(hubId)) return double.MaxValue;
            Vessel v = VesselById(hubId);
            if (v == null) return 0;
            double t = 0;
            foreach (Vessel h in LocalPool(v, exclude)) t += VesselSpace(h, res, cargoIntent);
            return t;
        }

        /// <summary>Take from a pool. At KSC this is a PURCHASE and may fail on funds. [V3]</summary>
        public static double PoolTake(string hubId, string res, double amount, Vessel exclude = null, bool cargoIntent = true)
        {
            if (amount <= 0) return 0;

            if (IsKSC(hubId))
            {
                PartResourceDefinition def = PartResourceLibrary.Instance.GetDefinition(res);
                double unit = def != null ? def.unitCost : 0;
                double cost = unit * amount;
                if (Funding.Instance != null && cost > 0)
                {
                    if (Funding.Instance.Funds < cost) return 0;   // cannot afford; never go into debt
                    Funding.Instance.AddFunds(-cost, TransactionReasons.VesselRollout);
                }
                return amount;   // KSC storage is infinite
            }

            Vessel v = VesselById(hubId);
            if (v == null) return 0;
            double remaining = amount;
            foreach (Vessel h in LocalPool(v, exclude))
            {
                if (remaining <= 0) break;
                remaining -= TakeFromVessel(h, res, remaining, cargoIntent);
            }
            return amount - remaining;
        }

        /// <summary>Add to a pool. At KSC this is a SALE and CREDITS funds. [V3]</summary>
        public static double PoolAdd(string hubId, string res, double amount, Vessel exclude = null, bool cargoIntent = true)
        {
            if (amount <= 0) return 0;

            if (IsKSC(hubId))
            {
                PartResourceDefinition def = PartResourceLibrary.Instance.GetDefinition(res);
                double unit = def != null ? def.unitCost : 0;
                if (Funding.Instance != null && unit > 0)
                    Funding.Instance.AddFunds(unit * amount, TransactionReasons.Vessels);
                return amount;
            }

            Vessel v = VesselById(hubId);
            if (v == null) return 0;
            double remaining = amount;
            foreach (Vessel h in LocalPool(v, exclude))
            {
                if (remaining <= 0) break;
                remaining -= AddToVessel(h, res, remaining, cargoIntent);
            }
            return amount - remaining;
        }

        // ================================================================
        // RECORDING
        // ================================================================
        /// <summary>Which hub is this vessel parked beside? Returns hub id, or null.</summary>
        public static string HubBeside(Vessel v)
        {
            if (v == null) return null;
            if (NearKSC(v)) return KSC_HUB;

            Vector3d p = HubPosition(v);
            foreach (Vessel h in AllHubVessels())
            {
                if (h == v) continue;
                if (h.mainBody != v.mainBody) continue;
                if (CrewCount(h) < 1) continue;                 // crew required at every hub
                if (Vector3d.Distance(HubPosition(h), p) > POOL_RANGE) continue;
                return h.id.ToString();
            }
            return null;
        }

        public bool StartRecording(Vessel v, out string reason)
        {
            reason = "";
            if (v == null) { reason = "No vessel."; return false; }
            if (Recordings.ContainsKey(v.id.ToString()))
            { reason = "This vessel is already recording a route."; return false; }

            string hub = HubBeside(v);
            if (hub == null) { reason = "No crewed logistics hub within " + POOL_RANGE + "m."; return false; }

            KASAActiveRecording rec = new KASAActiveRecording();
            rec.HaulerId = v.id.ToString();
            rec.HaulerName = v.vesselName;
            rec.StartHubId = hub;
            rec.BodyName = v.mainBody.bodyName;
            rec.StartUT = Planetarium.GetUniversalTime();
            rec.Sample = VesselResources(v);

            Recordings[rec.HaulerId] = rec;
            Debug.Log("[KASA] Logistics: recording started at hub " + HubDisplayName(hub));
            return true;
        }

        public void CancelRecording(Vessel v)
        {
            if (v == null) return;
            Recordings.Remove(v.id.ToString());
        }

        public bool CompleteRecording(Vessel v, out string reason)
        {
            reason = "";
            KASAActiveRecording rec;
            if (v == null || !Recordings.TryGetValue(v.id.ToString(), out rec))
            { reason = "This vessel is not recording a route."; return false; }
            if (!rec.MidpointDone)
            { reason = "The hauler has not yet reached a second hub."; return false; }

            string hub = HubBeside(v);
            if (hub == null)
            { reason = "Park within " + POOL_RANGE + "m of a crewed hub to close the route."; return false; }

            SampleFuel(rec, v);

            double now = Planetarium.GetUniversalTime();
            KASARoute route = new KASARoute();
            route.BodyName = rec.BodyName;
            route.Payload = new Dictionary<string, double>(rec.Loaded.Count > 0 ? rec.Loaded : rec.Peak);
            route.RecordedUT = now;
            route.HaulerName = v.vesselName;
            route.HubA = rec.StartHubId;
            route.HubB = rec.FarHubId;
            route.SourceHubId = rec.SourceHubId;   // direction = where you loaded -> where you unloaded

            if (hub == rec.StartHubId)
            {
                // ---- ROUND TRIP: hauler reusable, charged the measured fuel ----
                route.OneWay = false;
                route.HaulerId = rec.HaulerId;
                route.LegAB.Fuel = new Dictionary<string, double>(rec.Leg1);
                route.LegAB.Time = rec.MidpointUT - rec.StartUT;
                route.LegBA.Fuel = new Dictionary<string, double>(rec.Leg2);
                route.LegBA.Time = now - rec.MidpointUT;
            }
            else if (hub == rec.FarHubId)
            {
                // ---- ONE-WAY: hauler expended, charged its full editor cost ----
                // Do NOT also charge fuel: it is already inside VesselCost. [V1]
                route.OneWay = true;
                route.LegAB.Fuel = new Dictionary<string, double>(rec.Leg1);
                route.LegAB.Time = rec.MidpointUT - rec.StartUT;
                route.VesselCost = VesselCost(v);
                Debug.Log("[KASA] one-way cost snapshot for " + v.vesselName + " = " +
                          route.VesselCost.ToString("F0") + " funds");
            }
            else
            {
                reason = "Finish at the hub you started from (round trip), or at the far hub (one-way).";
                return false;
            }

            Recordings.Remove(rec.HaulerId);

            if (route.Capacity <= 0)
            { reason = "The hauler never carried any cargo, so no route was recorded."; return false; }

            KASARoute existing;
            if (Routes.TryGetValue(route.Id, out existing) &&
                existing.OneWay == route.OneWay &&
                existing.CostPerUnit <= route.CostPerUnit)
            {
                reason = "Route flown, but your previous recording was more efficient. Keeping the better one.";
                return true;
            }

            Routes[route.Id] = route;
            reason = (route.OneWay ? "One-way" : "Round-trip") + " route recorded: " +
                     HubDisplayName(route.HubA) + " -> " + HubDisplayName(route.HubB) + ", " +
                     route.Capacity.ToString("F0") + " units per run, " +
                     FormatTime(route.OneWay ? route.LegAB.Time : route.TotalTime) +
                     (route.OneWay
                        ? " one-way, " + route.VesselCost.ToString("F0") + " funds per run."
                        : " round trip.");
            Debug.Log("[KASA] " + reason);
            return true;
        }

        /// <summary>
        /// [V1] AvailablePart.cost is assumed to INCLUDE a full load of resources, so
        /// dry = ap.cost - sum(maxAmount * unitCost). We then add back what is ACTUALLY
        /// aboard. One-way routes charge this and nothing else — the fuel is already in it.
        /// </summary>
        public static double VesselCost(Vessel v)
        {
            if (v == null || !v.loaded) return 0;
            double total = 0;
            foreach (Part p in v.parts)
            {
                if (p.partInfo == null) continue;
                double dry = p.partInfo.cost;
                double loaded = 0;
                foreach (PartResource r in p.Resources)
                {
                    PartResourceDefinition def = PartResourceLibrary.Instance.GetDefinition(r.resourceName);
                    if (def == null) continue;
                    dry -= r.maxAmount * def.unitCost;
                    loaded += r.amount * def.unitCost;
                }
                total += Math.Max(dry, 0) + loaded;
            }
            return total;
        }

        private static Dictionary<string, double> VesselResources(Vessel v)
        {
            var d = new Dictionary<string, double>();
            if (v == null || !v.loaded || v.parts == null) return d;
            foreach (Part p in v.parts)
                foreach (PartResource r in p.Resources)
                {
                    if (!d.ContainsKey(r.resourceName)) d[r.resourceName] = 0;
                    d[r.resourceName] += r.amount;
                }
            return d;
        }

        /// <summary>Accumulate only DECREASES, so refuelling never flatters a route.</summary>
        private static void SampleFuel(KASAActiveRecording rec, Vessel v)
        {
            if (!v.loaded) return;   // unloaded = on rails = burning nothing
            var now = VesselResources(v);
            var sink = rec.MidpointDone ? rec.Leg2 : rec.Leg1;

            foreach (var kv in now)
            {
                if (IsPureCargo(kv.Key) || IgnoredForFuel.Contains(kv.Key)) continue;
                double before;
                if (!rec.Sample.TryGetValue(kv.Key, out before)) continue;
                double delta = before - kv.Value;
                if (delta > 0)
                {
                    if (!sink.ContainsKey(kv.Key)) sink[kv.Key] = 0;
                    sink[kv.Key] += delta;
                }
            }
            rec.Sample = now;
        }

        // ================================================================
        // DISPATCH
        // ================================================================
        /// <summary>Delete a route. Staged (loaded-but-not-departed) cargo is returned to the
        /// source so nothing is lost; any already-in-transit cargo still lands (dispatches carry
        /// their own dest/resource/amount); the hauler's busy lock clears itself.</summary>
        public void DeleteRoute(string routeId)
        {
            KASARoute r;
            if (!Routes.TryGetValue(routeId, out r)) return;
            if (r.Staged != null && r.Staged.Count > 0)
                foreach (var kv in r.Staged) PoolAdd(r.Source, kv.Key, kv.Value);
            Routes.Remove(routeId);
        }

        /// <summary>Charge a round trip's fuel for `runs` runs, hauler tanks first then the
        /// source pool. Returns false (with a reason) if it can't be paid; takes nothing then.</summary>
        /// <summary>Record cargo loaded onto a hauler mid-recording — this becomes the route payload.</summary>
        public void RecordLoaded(Vessel v, string res, double amount)
        {
            KASAActiveRecording rec;
            if (v == null || amount <= 0 || !Recordings.TryGetValue(v.id.ToString(), out rec)) return;
            double cur; rec.Loaded.TryGetValue(res, out cur); rec.Loaded[res] = cur + amount;
        }

        /// <summary>Rebase the fuel sample after a load/unload so the cargo jump is not counted as burn.</summary>
        public void ResetFuelSample(Vessel v)
        {
            KASAActiveRecording rec;
            if (v != null && Recordings.TryGetValue(v.id.ToString(), out rec)) rec.Sample = VesselResources(v);
        }

        private bool ChargeRoundTripFuel(KASARoute route, string fromHub, int runs, out string reason)
        {
            reason = "";
            Vessel hauler = VesselById(route.HaulerId);
            var fuel = route.RoundTripFuel();
            // Propellant comes from FEED tanks, never cargo holding tanks — cargoIntent:false —
            // so a tanker hauling LF/Ox cargo never burns its own load (or the hub's).
            foreach (var kv in fuel)
            {
                double need = kv.Value * runs;
                if (PoolAmount(fromHub, kv.Key, null, false) + VesselAmount(hauler, kv.Key, false) < need - 0.001)
                {
                    reason = "Needs " + need.ToString("F0") + " " + kv.Key + " for " + runs +
                             " run(s); the hauler and its local pool cannot supply it.";
                    return false;
                }
            }
            // Draw from the hauler's own feed tanks first, then its local pool's feed tanks.
            foreach (var kv in fuel)
            {
                double need = kv.Value * runs;
                need -= TakeFromVessel(hauler, kv.Key, need, false);
                if (need > 0.001) PoolTake(fromHub, kv.Key, need, null, false);
            }
            return true;
        }

        public bool Dispatch(KASARoute route, string fromHub, string res, double amount, out string reason)
        {
            reason = "";
            double now = Planetarium.GetUniversalTime();
            string toHub = (fromHub == route.HubA) ? route.HubB : route.HubA;

            if (route.OneWay && fromHub != route.HubA)
            {
                reason = "One-way route: cargo only flows " + HubDisplayName(route.HubA) +
                         " -> " + HubDisplayName(route.HubB) + ".";
                return false;
            }

            // Hauler lock-out. A shuttle in transit cannot fly a second dispatch.
            // This is what stops infinite storage sneaking back in as infinite throughput.
            if (!route.OneWay)
            {
                double until;
                if (HaulerBusyUntil.TryGetValue(route.HaulerId, out until) && until > now)
                { reason = "The hauler is in transit for another " + FormatTime(until - now) + "."; return false; }
                if (VesselById(route.HaulerId) == null)
                { reason = "The hauler no longer exists. Re-record the route, or reassign it."; return false; }
            }

            double available = PoolAmount(fromHub, res);
            if (available <= 0.01) { reason = HubDisplayName(fromHub) + " holds no " + res + "."; return false; }
            amount = Math.Min(amount, available);

            double space = PoolSpace(toHub, res);
            if (space <= 0.01) { reason = HubDisplayName(toHub) + " has no room for " + res + "."; return false; }
            amount = Math.Min(amount, space);

            // One dispatch = one hauler = discrete runs. A part-load still costs a full run.
            // Runs are sized against how much of THIS resource the hauler recorded
            // carrying, not the route's total capacity. Identical for single-resource
            // routes; correct for multi-resource ones (and for future LF/Ox routes).
            double perRun;
            if (!route.Payload.TryGetValue(res, out perRun) || perRun <= 0) perRun = route.Capacity;
            int runs = (int)Math.Ceiling(amount / Math.Max(perRun, 1.0));

            if (route.OneWay)
            {
                double cost = route.VesselCost * runs;
                if (Funding.Instance != null && cost > 0)
                {
                    if (Funding.Instance.Funds < cost)
                    {
                        reason = "Cannot afford " + runs + " freighter(s) at " +
                                 route.VesselCost.ToString("F0") + " funds each.";
                        return false;
                    }
                    Funding.Instance.AddFunds(-cost, TransactionReasons.VesselRollout);
                }
            }
            else
            {
                if (!ChargeRoundTripFuel(route, fromHub, runs, out reason)) return false;
                HaulerBusyUntil[route.HaulerId] = now + route.TotalTime * runs;
            }

            double taken = PoolTake(fromHub, res, amount);
            if (taken <= 0) { reason = "Could not draw " + res + " from " + HubDisplayName(fromHub) + "."; return false; }

            KASADispatch d = new KASADispatch();
            d.RouteId = route.Id;
            d.DestHubId = toHub;
            d.Resource = res;
            d.Amount = taken;
            d.ArrivalUT = now + route.TimeFrom(fromHub) +
                          (route.OneWay ? 0 : route.TotalTime * (runs - 1));
            Dispatches.Add(d);

            reason = runs + " run(s): " + taken.ToString("F0") + " " + res + " to " +
                     HubDisplayName(toHub) + ", arriving in " + FormatTime(d.ArrivalUT - now) + ".";
            return true;
        }

        private void DeliverDispatch(KASADispatch d)
        {
            double placed = PoolAdd(d.DestHubId, d.Resource, d.Amount);
            double spill = d.Amount - placed;

            if (spill > 0.01)
            {
                // Destination gone or full: return it to the origin rather than delete it.
                KASARoute r;
                if (Routes.TryGetValue(d.RouteId, out r))
                {
                    string origin = (d.DestHubId == r.HubB) ? r.HubA : r.HubB;
                    PoolAdd(origin, d.Resource, spill);
                }
                Debug.LogWarning("[KASA] Logistics: " + spill.ToString("F0") + " " + d.Resource +
                                 " undeliverable; returned to origin.");
            }
            Debug.Log("[KASA] Logistics: delivered " + placed.ToString("F0") + " " +
                      d.Resource + " to " + HubDisplayName(d.DestHubId));
        }

        // ================================================================
        // STANDING ORDERS
        // Foreground only until the background sim (step 4) exists.
        // ================================================================
        // ACTIVE ROUTES — the automation. A round-trip route toggled Active cycles:
        //   Idle -> load a full (or, if !WaitForFull, partial) load at the source, which
        //   drains the source so it keeps producing -> Staged (hold loaded at the source)
        //   -> when the destination has room, charge fuel and fly the delivery leg ->
        //   InFlight (deliver, then return) -> back to Idle. The two waits surface as
        //   distinct status lines the player can act on.
        // ================================================================
        private void TickActiveRoutes()
        {
            double now = Planetarium.GetUniversalTime();

            foreach (KASARoute r in Routes.Values)
            {
                if (!r.Active) continue;
                if (r.OneWay) { r.Active = false; continue; }   // active = round trip only

                string src = r.Source, dst = r.Dest;
                if (VesselById(r.HaulerId) == null) { r.LastStatus = "hauler missing"; continue; }

                switch (r.Phase)
                {
                    case 0: // Idle — try to load at the source
                        {
                            var load = new Dictionary<string, double>();
                            bool full = true; double total = 0;
                            foreach (var kv in r.Payload)
                            {
                                double take = Math.Min(PoolAmount(src, kv.Key), kv.Value);
                                if (take < kv.Value - 0.01) full = false;
                                load[kv.Key] = take; total += take;
                            }
                            if ((r.WaitForFull && !full) || total <= 0.01) { r.LastStatus = "waiting for cargo"; break; }

                            r.Staged = new Dictionary<string, double>();
                            foreach (var kv in load)
                            {
                                if (kv.Value <= 0.01) continue;
                                double got = PoolTake(src, kv.Key, kv.Value);
                                if (got > 0) r.Staged[kv.Key] = got;
                            }
                            r.Phase = 1;
                            r.LastStatus = "loaded, holding";
                            break;
                        }
                    case 1: // Staged — loaded, hold at source until destination has room
                        {
                            bool room = true;
                            foreach (var kv in r.Staged)
                                if (PoolSpace(dst, kv.Key) < kv.Value - 0.01) { room = false; break; }
                            if (!room) { r.LastStatus = "staged: destination full"; break; }

                            string reason;
                            if (!ChargeRoundTripFuel(r, src, 1, out reason)) { r.LastStatus = reason; break; }

                            double deliveryUT = now + r.TimeFrom(src);
                            foreach (var kv in r.Staged)
                            {
                                KASADispatch d = new KASADispatch();
                                d.RouteId = r.Id; d.DestHubId = dst;
                                d.Resource = kv.Key; d.Amount = kv.Value;
                                d.ArrivalUT = deliveryUT;
                                Dispatches.Add(d);
                            }
                            HaulerBusyUntil[r.HaulerId] = now + r.TotalTime;   // delivery + return
                            r.Staged = new Dictionary<string, double>();
                            r.Phase = 2;
                            r.LastStatus = "in transit";
                            break;
                        }
                    case 2: // InFlight — cargo delivered mid-way; wait out the return leg
                        {
                            double until;
                            if (HaulerBusyUntil.TryGetValue(r.HaulerId, out until) && until > now)
                                r.LastStatus = "in transit (" + FormatTime(until - now) + ")";
                            else { r.Phase = 0; r.LastStatus = "ready"; }
                            break;
                        }
                }
            }
        }

        // ================================================================
        // UPDATE
        // ================================================================
        private void Update()
        {
            double now = Planetarium.GetUniversalTime();

            // --- sample every recording whose hauler is currently loaded ---
            if (HighLogic.LoadedSceneIsFlight && now - lastSampleUT >= SAMPLE_INTERVAL)
            {
                lastSampleUT = now;
                foreach (var rec in Recordings.Values.ToList())
                {
                    Vessel v = VesselById(rec.HaulerId);
                    if (v == null || !v.loaded) continue;   // on rails: burning nothing, so nothing is missed

                    SampleFuel(rec, v);

                    if (!rec.MidpointDone)
                    {
                        string hub = HubBeside(v);
                        if (hub != null && hub != rec.StartHubId)
                        {
                            rec.MidpointDone = true;
                            rec.FarHubId = hub;
                            rec.MidpointUT = now;
                            ScreenMessages.PostScreenMessage(
                                "[KASA] Leg recorded to " + HubDisplayName(hub) + " (" +
                                FormatTime(now - rec.StartUT) + "). Stop here for a ONE-WAY route, " +
                                "or return to " + HubDisplayName(rec.StartHubId) + " for a ROUND TRIP.",
                                10f, ScreenMessageStyle.UPPER_CENTER);
                        }
                    }
                }
            }

            // --- land arrived cargo ---
            for (int i = Dispatches.Count - 1; i >= 0; i--)
            {
                if (Dispatches[i].ArrivalUT > now) continue;
                DeliverDispatch(Dispatches[i]);
                Dispatches.RemoveAt(i);
            }

            // --- free up haulers whose run has completed ---
            foreach (var key in HaulerBusyUntil.Where(k => k.Value <= now).Select(k => k.Key).ToList())
                HaulerBusyUntil.Remove(key);

            // --- active routes ---
            if (now - lastRouteUT >= ROUTE_INTERVAL)
            {
                lastRouteUT = now;
                TickActiveRoutes();
            }
        }

        public static string FormatTime(double s)
        {
            if (s < 60) return s.ToString("F0") + "s";
            double days = s / KSPUtil.dateTimeFormatter.Day;
            if (days >= 1) return days.ToString("F1") + "d";
            return (s / 3600.0).ToString("F1") + "h";
        }
    }


    // ================================================================
    // ================================================================
    // CARGO TANK — marker on KASA holding tanks. Lets dual-use resources
    // (LF/Ox) held as CARGO be told apart from the same resource burned as
    // FUEL in a feed tank. MM-patched onto the holding tanks.
    // ================================================================
    public class KASACargoTank : PartModule
    {
        public override string GetInfo() { return "KASA cargo hold (routable storage)"; }
    }


    // HUB — marker only, MM-patched onto command parts (see KASA_Logistics.cfg).
    // All hub controls live in the toolbar window (KASALogisticsUI).
    // ================================================================
    public class KASALogisticsHub : PartModule
    {
        // Marker only. Every logistics control now lives in the toolbar window
        // (KASALogisticsUI). This module's sole job is to mark a vessel as a hub
        // candidate; registration (KASALogisticsScenario.RegisteredHubs) decides
        // which candidates actually act as hubs. MM-patched onto command parts.
        public override string GetInfo() { return "KASA Logistics Hub (register in the Logistics window)"; }
    }


    // ================================================================
    // TUG — MM-patched onto command parts
    // ================================================================
    public class KASALogisticsTug : PartModule
    {
        [KSPEvent(guiActive = true, guiName = "Start Logistics Run")]
        public void StartRun()
        {
            var s = KASALogisticsScenario.Instance;
            if (s == null) return;
            string reason;
            if (s.StartRecording(vessel, out reason))
                Msg("Recording. Fly to another hub. Stop there for a one-way route, or return here for a round trip.");
            else Msg(reason);
        }

        [KSPEvent(guiActive = true, guiName = "Complete Logistics Run")]
        public void CompleteRun()
        {
            var s = KASALogisticsScenario.Instance;
            if (s == null) return;
            string reason;
            s.CompleteRecording(vessel, out reason);
            Msg(reason);
        }

        [KSPEvent(guiActive = true, guiName = "Cancel Logistics Run")]
        public void CancelRun()
        {
            var s = KASALogisticsScenario.Instance;
            if (s == null) return;
            s.CancelRecording(vessel);
            Msg("Recording cancelled.");
        }

        /// <summary>
        /// Fill this vessel's tanks from the adjacent crewed hub's pool. The ferry's own
        /// tank configuration decides WHAT loads (only resources it has space for); the
        /// player decides WHEN. Needs no recorded route — this is the manual "connect the
        /// hoses" step that a recording pass needs to capture a real peak payload, and the
        /// only way to load an uncrewed tug (which is never a pool member). At KSC the pool
        /// is infinite and this is charged as a purchase.
        /// </summary>
        [KSPEvent(guiActive = true, guiName = "Load Cargo From Hub")]
        public void LoadFromHub()
        {
            string hub = KASALogisticsScenario.HubBeside(vessel);
            if (hub == null) { Msg("No crewed hub within 1km to load from."); return; }

            // Direction capture: the hub you first load at is the route's source.
            KASAActiveRecording rec;
            if (KASALogisticsScenario.Instance != null &&
                KASALogisticsScenario.Instance.Recordings.TryGetValue(vessel.id.ToString(), out rec) &&
                string.IsNullOrEmpty(rec.SourceHubId))
                rec.SourceHubId = hub;

            double totalLoaded = 0;
            string last = "";
            foreach (string res in KASALogisticsScenario.CargoResources)
            {
                double space = KASALogisticsScenario.VesselSpace(vessel, res);
                if (space <= 0.01) continue;                       // no tank for this resource
                double avail = KASALogisticsScenario.PoolAmount(hub, res, vessel);
                double want = Math.Min(space, avail);
                if (want <= 0.01) continue;

                double taken = KASALogisticsScenario.PoolTake(hub, res, want, vessel);
                double placed = KASALogisticsScenario.AddToVessel(vessel, res, taken);
                if (placed < taken - 0.001)                        // safety: return the overflow
                    KASALogisticsScenario.PoolAdd(hub, res, taken - placed, vessel);

                if (placed > 0.01)
                {
                    totalLoaded += placed; last = res;
                    KASALogisticsScenario.Instance.RecordLoaded(vessel, res, placed);
                }
            }

            KASALogisticsScenario.Instance.ResetFuelSample(vessel);   // don't count the load as burn

            if (totalLoaded <= 0.01)
                Msg("Nothing to load - no matching cargo in the pool, or tanks already full.");
            else
                Msg("Loaded " + totalLoaded.ToString("F0") + " units from " +
                    KASALogisticsScenario.HubDisplayName(hub) +
                    (last != "" ? " (last: " + last + ")." : "."));
        }

        /// <summary>
        /// Push this vessel's cargo into the adjacent crewed hub's pool. Lets a round-trip
        /// return leg be flown (and measured) empty. At KSC this is a sale and credits funds.
        /// </summary>
        [KSPEvent(guiActive = true, guiName = "Unload Cargo To Hub")]
        public void UnloadToHub()
        {
            string hub = KASALogisticsScenario.HubBeside(vessel);
            if (hub == null) { Msg("No crewed hub within 1km to unload to."); return; }

            double totalUnloaded = 0;
            foreach (string res in KASALogisticsScenario.CargoResources)
            {
                double have = KASALogisticsScenario.VesselAmount(vessel, res);
                if (have <= 0.01) continue;
                double room = KASALogisticsScenario.PoolSpace(hub, res, vessel);
                double give = Math.Min(have, room);
                if (give <= 0.01) continue;

                double taken = KASALogisticsScenario.TakeFromVessel(vessel, res, give);
                double placed = KASALogisticsScenario.PoolAdd(hub, res, taken, vessel);
                if (placed < taken - 0.001)                        // safety: put back what didn't fit
                    KASALogisticsScenario.AddToVessel(vessel, res, taken - placed);

                totalUnloaded += placed;
            }

            KASALogisticsScenario.Instance.ResetFuelSample(vessel);   // don't count the unload as burn

            if (totalUnloaded <= 0.01)
                Msg("Nothing to unload - hold empty, or the hub has no room.");
            else
                Msg("Unloaded " + totalUnloaded.ToString("F0") + " units to " +
                    KASALogisticsScenario.HubDisplayName(hub) + ".");
        }

        /// <summary>[V2] Escape hatch if the Guid migration missed a docking/undocking.</summary>
        [KSPEvent(guiActive = true, guiName = "Reassign Hauler To This Vessel")]
        public void Reassign()
        {
            var s = KASALogisticsScenario.Instance;
            if (s == null) return;
            string me = vessel.id.ToString();
            int n = 0;
            foreach (var r in s.Routes.Values)
                if (!r.OneWay && KASALogisticsScenario.VesselById(r.HaulerId) == null)
                { r.HaulerId = me; r.HaulerName = vessel.vesselName; n++; }
            Msg(n > 0 ? "Reassigned " + n + " orphaned route(s) to this vessel." : "No orphaned routes found.");
        }

        public override void OnUpdate()
        {
            var s = KASALogisticsScenario.Instance;
            if (s == null || vessel == null) return;
            bool rec = s.Recordings.ContainsKey(vessel.id.ToString());
            Events["StartRun"].active = !rec;
            Events["CompleteRun"].active = rec;
            Events["CancelRun"].active = rec;
        }

        private static void Msg(string s)
        {
            ScreenMessages.PostScreenMessage("[KASA] " + s, 9f, ScreenMessageStyle.UPPER_CENTER);
        }

        public override string GetInfo() { return "KASA Logistics Tug"; }
    }
}