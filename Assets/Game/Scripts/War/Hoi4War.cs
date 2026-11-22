using System;
using System.Collections;
using System.Collections.Generic;
using Core;
using Core.Systems;
using Core.Units;
using Hoi4.Map;
using Hoi4.Units;
using UnityEngine;
using FP = Core.Data.FixedPoint64;

namespace Hoi4.War
{
    /// <summary>
    /// GAME: HOI4 war outcome: war participation, surrender progress and capitulation. Prototype and the
    /// numbers behind them: Tools/prototypes/peace/peace_conference.py.
    /// - War participation points, per country against each enemy (common/defines NMilitary / NDiplomacy):
    ///   0.1 per strength point of damage dealt (WAR_SCORE_LAND_DAMAGE_FACTOR), 0.5 per 1,000 casualties inflicted
    ///   (WAR_SCORE_LOSSES_RATIO), and for every province taken 0.2 per victory point (VICTORY_POINT_WORTH_FACTOR_WARSCORE)
    ///   plus 0.2 x its share of its state's value (PROVINCE_WORTH_FROM_STATE_VALUE_FACTOR_WARSCORE). A winner's
    ///   participation is its share of all the winners' points against the loser.
    /// - Surrender progress: the share of a country's victory points held by enemies, over the surrender limit
    ///   (BASE_SURRENDER_LIMIT 0.8). Without victory points, provinces count instead.
    /// - Capitulation at 100%, once the war is 7 days old (DAYS_OF_WAR_BEFORE_SURRENDER): the loser's divisions
    ///   disband, and a peace conference opens among everyone at war with it (Hoi4PeaceConference). With the player
    ///   among the winners the game pauses for the conference screen; otherwise the AI decides it at once.
    /// Daily from Hoi4Simulation, countries and wars in id order: the same on every machine.
    /// </summary>
    public sealed class Hoi4War : MonoBehaviour
    {
        public static readonly FP DamageFactor = FP.FromFraction(1, 10);
        public static readonly FP LossesPerThousand = FP.Half;
        public static readonly FP VictoryPointWorth = FP.FromFraction(2, 10);
        public static readonly FP StateValueWorth = FP.FromFraction(2, 10);
        public static readonly FP SurrenderLimit = FP.FromFraction(8, 10);
        public const int DaysBeforeSurrender = 7;

        public static Hoi4War Instance { get; private set; }

        GameState gameState;
        IDisposable[] subscriptions;
        readonly Dictionary<(ushort country, ushort enemy), FP> points = new Dictionary<(ushort, ushort), FP>();
        readonly Dictionary<(ushort, ushort), int> warStart = new Dictionary<(ushort, ushort), int>(); // (low id, high id) -> day
        readonly Dictionary<ushort, int> victoryPoints = new Dictionary<ushort, int>();
        int day;

        /// <summary>The player's conference, open while the conference screen shows.</summary>
        public Hoi4PeaceConference Conference { get; private set; }
        public event Action<Hoi4PeaceConference> ConferenceOpened;
        public event Action ConferenceClosed;
        bool resumeAfterConference;

        public bool IsReady => gameState != null;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Spawn()
        {
            if (!Hoi4.Hoi4Scenes.Active) return; // not in other scenes (the engine's StarterKit scene)
            var go = new GameObject("Hoi4War");
            DontDestroyOnLoad(go);
            go.AddComponent<Hoi4War>();
        }

        void Awake() => Instance = this;

        void OnDestroy()
        {
            if (subscriptions != null) foreach (var s in subscriptions) s?.Dispose();
            if (ownedBuffer.IsCreated) ownedBuffer.Dispose();
            if (Instance == this) Instance = null;
        }

        IEnumerator Start()
        {
            while (Engine.ArchonEngine.Instance == null || Engine.ArchonEngine.Instance.GameState == null
                   || Engine.ArchonEngine.Instance.GameState.Diplomacy == null || !Hoi4StateData.Available || !Hoi4Regions.Available)
                yield return null;
            gameState = Engine.ArchonEngine.Instance.GameState;
            foreach (int s in Hoi4Regions.States)
            {
                var info = Hoi4StateData.Get(s);
                if (info == null) continue;
                foreach (var kv in info.VictoryPoints) victoryPoints[(ushort)kv.Key] = kv.Value;
            }
            subscriptions = new[]
            {
                gameState.EventBus.Subscribe<ProvinceControllerChangedEvent>(e => OnControllerChanged(e.ProvinceId, e.OldController, e.NewController)),
            };
        }

        static ushort Player => StarterKit.Initializer.Instance?.PlayerState?.PlayerCountryId ?? 0;

        /// <summary>A province's victory points (0 when none).</summary>
        public int VictoryPoints(ushort province) => victoryPoints.TryGetValue(province, out int v) ? v : 0;

        // --- War participation --------------------------------------------------------------------------

        /// <summary>Combat (Hoi4Combat, hourly): the damage one side's front did to the other's. Shared evenly over the
        /// dealers' and the victims' divisions, by country pair.</summary>
        public void CreditDamage(List<Hoi4Division> dealers, List<Hoi4Division> victims, FP strength, FP men)
        {
            if (gameState == null || dealers.Count == 0 || victims.Count == 0) return;
            FP total = strength * DamageFactor + men / 1000 * LossesPerThousand;
            if (total <= FP.Zero) return;
            FP each = total / (dealers.Count * victims.Count);
            var units = gameState.Units;
            foreach (var a in dealers)
            {
                if (!units.HasUnit(a.UnitId)) continue;
                ushort ca = units.GetUnit(a.UnitId).countryID;
                foreach (var v in victims)
                {
                    if (!units.HasUnit(v.UnitId)) continue;
                    Add(ca, units.GetUnit(v.UnitId).countryID, each);
                }
            }
        }

        void OnControllerChanged(ushort province, ushort oldController, ushort newController)
        {
            if (oldController == 0 || newController == 0 || oldController == newController) return;
            if (!gameState.Diplomacy.IsAtWar(newController, oldController)) return;
            int state = Hoi4Regions.State(province);
            var info = Hoi4StateData.Get(state);
            int provinces = Math.Max(1, Hoi4Regions.ProvincesOf(state).Count);
            FP worth = VictoryPointWorth * VictoryPoints(province)
                       + (info != null ? StateValueWorth * FP.FromDouble(info.Value) / provinces : FP.Zero);
            Add(newController, oldController, worth);
        }

        void Add(ushort country, ushort enemy, FP v)
        {
            if (country == enemy || v <= FP.Zero) return;
            points[(country, enemy)] = (points.TryGetValue((country, enemy), out FP p) ? p : FP.Zero) + v;
        }

        /// <summary>A country's participation points against an enemy.</summary>
        public FP Points(ushort country, ushort enemy) => points.TryGetValue((country, enemy), out FP p) ? p : FP.Zero;

        /// <summary>Each winner's share of the war against the loser (equal shares when nobody scored). Winners in id order.</summary>
        public List<(ushort country, FP share)> Participation(ushort loser, List<ushort> winners)
        {
            var result = new List<(ushort, FP)>();
            FP sum = FP.Zero;
            foreach (ushort w in winners) sum += Points(w, loser);
            foreach (ushort w in winners)
                result.Add((w, sum > FP.Zero ? Points(w, loser) / sum : FP.One / winners.Count));
            return result;
        }

        // --- Surrender ----------------------------------------------------------------------------------

        /// <summary>Surrender progress, 0..1 (capitulation at 1): the share of the country's victory points its
        /// enemies hold, over the surrender limit.</summary>
        Unity.Collections.NativeList<ushort> ownedBuffer;

        public FP SurrenderProgress(ushort country)
        {
            if (!ownedBuffer.IsCreated) ownedBuffer = new Unity.Collections.NativeList<ushort>(1024, Unity.Collections.Allocator.Persistent);
            if (gameState == null || country == 0) return FP.Zero;
            int total = 0, held = 0, provinces = 0, heldProvinces = 0;
            var diplomacy = gameState.Diplomacy;
            // the engine's owner->provinces reverse index: only this country's provinces, not the whole map
            ownedBuffer.Clear();
            gameState.Provinces.GetCountryProvinces(country, ownedBuffer);
            for (int i = 0; i < ownedBuffer.Length; i++)
            {
                ushort p = ownedBuffer[i];
                if (Hoi4Regions.IsWater(p)) continue;
                ushort c = Hoi4Occupation.ControllerOf(gameState, p);
                bool enemy = c != country && diplomacy.IsAtWar(country, c);
                int vp = VictoryPoints(p);
                total += vp; provinces++;
                if (enemy) { held += vp; heldProvinces++; }
            }
            if (total == 0) { total = provinces; held = heldProvinces; }
            if (total == 0) return FP.Zero;
            return FP.Min(FP.One, FP.FromInt(held) / total / SurrenderLimit);
        }

        /// <summary>Daily, from Hoi4Simulation: war start dates, then capitulations.</summary>
        public void SimulateDay()
        {
            if (gameState == null || Conference != null) return; // nothing new while the player decides a peace
            day++;
            var wars = gameState.Diplomacy.GetAllWars();
            var current = new HashSet<(ushort, ushort)>();
            foreach (var (a, b) in wars)
            {
                var key = a < b ? (a, b) : (b, a);
                current.Add(key);
                if (!warStart.ContainsKey(key)) warStart[key] = day;
            }
            foreach (var key in new List<(ushort, ushort)>(warStart.Keys))
                if (!current.Contains(key)) { warStart.Remove(key); points.Remove((key.Item1, key.Item2)); points.Remove((key.Item2, key.Item1)); }

            var atWar = new SortedSet<ushort>();
            foreach (var (a, b) in current) { atWar.Add(a); atWar.Add(b); }
            foreach (ushort c in atWar)
            {
                if (SurrenderProgress(c) < FP.One) continue;
                bool oldEnough = false;
                foreach (var kv in warStart)
                    if ((kv.Key.Item1 == c || kv.Key.Item2 == c) && day - kv.Value >= DaysBeforeSurrender) { oldEnough = true; break; }
                if (!oldEnough) continue;
                Capitulate(c);
                if (Conference != null) return; // the player's conference first; others on later days
            }
        }

        void Capitulate(ushort loser)
        {
            var winners = new List<ushort>();
            foreach (var (a, b) in gameState.Diplomacy.GetAllWars())
            {
                if (a == loser && !winners.Contains(b)) winners.Add(b);
                if (b == loser && !winners.Contains(a)) winners.Add(a);
            }
            winners.Sort();
            if (winners.Count == 0) return;

            // Its army is gone
            var divisions = Hoi4Divisions.Instance;
            var units = gameState.Units.GetCountryUnits(loser);
            units.Sort();
            foreach (ushort u in units)
            {
                divisions?.Remove(u);
                gameState.Units.DisbandUnit(u, DestructionReason.Event);
            }

            // HOI4: a capitulating country hands its land to the enemy it capitulates to (the winner holding its
            // capital, else the one with the most participation), so the whole country is on the table
            var participation = Participation(loser, winners);
            ushort recipient = CapitulatesTo(loser, participation);
            var provinceSystem = gameState.Provinces;
            foreach (int s in Hoi4Regions.States)
                foreach (ushort p in Hoi4Regions.ProvincesOf(s))
                {
                    if (provinceSystem.GetProvinceOwner(p) != loser) continue;
                    ushort c = Hoi4Occupation.ControllerOf(gameState, p);
                    if (!winners.Contains(c)) provinceSystem.SetProvinceController(p, recipient);
                }

            var conference = new Hoi4PeaceConference(gameState, loser, participation);
            Core.Recording.SessionRecorder.Note("war", "capitulated", loser, 0, 0, 0,
                $"{units.Count} divisions disbanded; conference: {conference.Describe()}");
            ushort player = Player;
            if (player != 0 && winners.Contains(player))
            {
                Conference = conference;
                var time = gameState.Time;
                resumeAfterConference = !time.IsPaused;
                if (!time.IsPaused) time.PauseTime();
                ConferenceOpened?.Invoke(conference);
            }
            else
            {
                conference.Conclude(new List<(int, ushort)>());
            }
        }

        /// <summary>Who the loser capitulates to: the winner holding its capital, else the one with the most
        /// participation (ties: the lower id).</summary>
        ushort CapitulatesTo(ushort loser, List<(ushort country, FP share)> participation)
        {
            ushort capital = Hoi4Regions.Capital(gameState.Countries.GetCountryTag(loser));
            if (capital != 0)
            {
                ushort holder = Hoi4Occupation.ControllerOf(gameState, capital);
                foreach (var w in participation) if (w.country == holder) return holder;
            }
            ushort best = participation[0].country;
            FP most = participation[0].share;
            foreach (var w in participation) if (w.share > most) { most = w.share; best = w.country; }
            return best;
        }

        /// <summary>PeaceDealCommand: the player's demands; the AI winners make theirs, the deal is carried out.</summary>
        public void ConcludeConference(ushort loser, List<(int state, ushort taker)> demands)
        {
            var conference = Conference;
            if (conference == null || conference.Loser != loser) return;
            conference.Conclude(demands);
            Conference = null;
            ConferenceClosed?.Invoke();
            if (resumeAfterConference && gameState.Time.IsPaused) gameState.Time.TogglePause();
            resumeAfterConference = false;
        }
    }
}
