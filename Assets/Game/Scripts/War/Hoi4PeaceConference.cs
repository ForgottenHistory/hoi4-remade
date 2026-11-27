using System.Collections.Generic;
using System.IO;
using Core;
using Core.Loaders;
using Core.Modding;
using Hoi4.Map;
using Hoi4.Units;
using Newtonsoft.Json.Linq;
using FP = Core.Data.FixedPoint64;

namespace Hoi4.War
{
    /// <summary>
    /// GAME: a HOI4 peace conference, the basic version: one turn, Take State demands only, no contests. Numbers from the
    /// HOI4 wiki and common/defines, checked against a live conference (Tools/prototypes/peace/peace_conference.py):
    /// - Score: 1.35 (PEACE_SCORE_SCALE_FACTOR) x the value of the loser's states the winners control, each winner's
    ///   share by war participation (Hoi4War), all of it at once.
    /// - Take-state cost: state value x ideology (the negotiator democratic 1.3, fascist 0.9, else 1.0) x occupation
    ///   (0.8 when it controls the state) x influence, or x 10 when no winner controls it
    ///   (BASE_PEACE_TAKE_UNCONTROLLED_STATE_FACTOR); capped at 9999.
    /// - Influence: 0.05 x f(capital) + 0.45 x f(nearest own core state) + 0.5 x f(nearest state it controls or has
    ///   demanded), f(d) = 0.7 + 0.3 x min(d / neutral, 1), d = state-centre map px / 22, neutral 30 / 6 / 14; minus 0.05
    ///   per adjacent state it has demanded (INFLUENCE_* defines).
    /// - The AI winners (and the player's leftovers are not taken for them): each in id order demands the states it
    ///   controls, best value per cost first, while its score lasts.
    /// - Conclusion: demanded states go to their takers (owner and controller); the loser keeps the rest and gets its
    ///   occupied land back, the winners get back what it held of theirs; the wars end; divisions left on land they
    ///   may not be on go to their capital (SendHome).
    /// Hoi4War hands all of the loser's land to a winner on capitulation (HOI4), so every state counts for the score.
    /// All maths is fixed point, in id order: the same on every machine.
    /// </summary>
    public sealed class Hoi4PeaceConference
    {
        public static readonly FP ScoreFactor = FP.FromFraction(135, 100);
        static readonly FP Uncontrolled = FP.FromInt(10);
        static readonly FP Occupation = FP.FromFraction(8, 10);
        static readonly FP MaxCost = FP.FromInt(9999);
        static readonly FP Divisor = FP.FromInt(22);
        static readonly FP Min = FP.FromFraction(7, 10), Span = FP.FromFraction(3, 10);
        static readonly FP NeutralCapital = FP.FromInt(30), NeutralCore = FP.FromInt(6), NeutralControlled = FP.FromInt(14);
        static readonly FP RatioCapital = FP.FromFraction(5, 100), RatioCore = FP.FromFraction(45, 100), RatioControlled = FP.Half;
        static readonly FP PerAdjacent = FP.FromFraction(5, 100);

        public sealed class Winner
        {
            public ushort Country;
            public FP Participation;   // 0..1
            public FP Score;           // conference score it gets
        }

        readonly GameState gameState;
        public ushort Loser { get; }
        public List<Winner> Winners { get; } = new List<Winner>();
        /// <summary>The loser's states (sorted), the ones it owns provinces in.</summary>
        public List<int> States { get; } = new List<int>();
        public FP TotalScore { get; }

        readonly Dictionary<int, ushort> stateController = new Dictionary<int, ushort>(); // most of its provinces
        readonly Dictionary<int, HashSet<int>> stateNeighbours = new Dictionary<int, HashSet<int>>();

        public Hoi4PeaceConference(GameState gameState, ushort loser, List<(ushort country, FP share)> participation)
        {
            this.gameState = gameState;
            Loser = loser;
            foreach (int s in Hoi4Regions.States)
            {
                var provinces = Hoi4Regions.ProvincesOf(s);
                bool owns = false;
                foreach (ushort p in provinces) if (gameState.Provinces.GetProvinceOwner(p) == loser) { owns = true; break; }
                if (owns) States.Add(s);
            }
            FP controlledValue = FP.Zero;
            foreach (int s in States)
            {
                stateController[s] = ControllerOf(s);
                if (IsWinner(stateController[s], participation)) controlledValue += Value(s);
            }
            TotalScore = ScoreFactor * controlledValue;
            foreach (var (country, share) in participation)
                Winners.Add(new Winner { Country = country, Participation = share, Score = TotalScore * share });
        }

        static bool IsWinner(ushort c, List<(ushort country, FP share)> participation)
        {
            foreach (var w in participation) if (w.country == c) return true;
            return false;
        }

        public Winner WinnerOf(ushort country) => Winners.Find(w => w.Country == country);

        public string Describe()
        {
            var parts = new List<string>();
            foreach (var w in Winners)
                parts.Add($"{Tag(w.Country)} {(w.Participation * 100).ToInt()}% score {w.Score.ToInt()}");
            return $"{Tag(Loser)} lost {States.Count} states, total score {TotalScore.ToInt()}: {string.Join(", ", parts)}";
        }

        string Tag(ushort c) => gameState.Countries.GetCountryTag(c);

        public static FP Value(int state)
        {
            var info = Hoi4StateData.Get(state);
            return info != null ? FP.FromDouble(info.Value) : FP.Zero;
        }

        /// <summary>Who holds most of the state's land provinces (ties: the lower id).</summary>
        ushort ControllerOf(int state)
        {
            var counts = new SortedDictionary<ushort, int>();
            foreach (ushort p in Hoi4Regions.ProvincesOf(state))
            {
                if (Hoi4Regions.IsWater(p)) continue;
                ushort c = Hoi4Occupation.ControllerOf(gameState, p);
                counts[c] = (counts.TryGetValue(c, out int n) ? n : 0) + 1;
            }
            ushort best = 0; int most = 0;
            foreach (var kv in counts) if (kv.Value > most) { most = kv.Value; best = kv.Key; }
            return best;
        }

        public ushort Controller(int state) => stateController.TryGetValue(state, out ushort c) ? c : (ushort)0;

        // --- Costs ---------------------------------------------------------------------------------------------

        /// <summary>What taking the state costs the negotiator, given the states it has already demanded.</summary>
        public FP Cost(int state, ushort negotiator, ICollection<int> demandedByIt)
        {
            FP cost = Value(state) * IdeologyFactor(negotiator);
            ushort controller = Controller(state);
            if (controller == negotiator) cost *= Occupation;
            else if (!IsWinner(controller)) cost *= Uncontrolled;
            cost *= Influence(state, negotiator, demandedByIt);
            return FP.Min(cost, MaxCost);
        }

        bool IsWinner(ushort c) => Winners.Exists(w => w.Country == c);

        FP Influence(int state, ushort negotiator, ICollection<int> demanded)
        {
            var info = Hoi4StateData.Get(state);
            if (info == null || !info.HasCenter) return FP.One;
            string tag = Tag(negotiator);
            int capitalState = Hoi4Regions.State(Hoi4Regions.Capital(tag));
            long capital = long.MaxValue, core = long.MaxValue, controlled = long.MaxValue;
            if (capitalState > 0) capital = Dist2(info, Hoi4StateData.Get(capitalState));
            // the states the negotiator owns or holds (Reach), then the ones it has demanded: a demanded state counts
            // as controlled and never as an owned core, also when it is among the first
            foreach (var r in ReachOf(negotiator, tag))
            {
                if (r.State == state) continue;
                bool mine = r.InConference && demanded != null && demanded.Contains(r.State);
                long d2 = Dist2(info, r.Info);
                if (mine || r.Holds) controlled = System.Math.Min(controlled, d2);
                if (!mine && r.OwnedCore) core = System.Math.Min(core, d2);
            }
            if (demanded != null)
                foreach (int s in demanded)
                {
                    if (s == state) continue;
                    var other = Hoi4StateData.Get(s);
                    if (other == null || !other.HasCenter) continue;
                    controlled = System.Math.Min(controlled, Dist2(info, other));
                }
            FP v = RatioCapital * Falloff(capital, NeutralCapital) + RatioCore * Falloff(core, NeutralCore)
                   + RatioControlled * Falloff(controlled, NeutralControlled);
            int adjacent = 0;
            if (demanded != null)
                foreach (int n in Neighbours(state)) if (demanded.Contains(n)) adjacent++;
            return FP.Max(FP.Zero, v - PerAdjacent * adjacent);
        }

        /// <summary>A state the negotiator owns or holds, for the influence distances.</summary>
        sealed class Reach
        {
            public int State;
            public Hoi4StateData.StateInfo Info;
            public bool Holds;          // it controls the state
            public bool OwnedCore;      // it owns the state and has a core on it
            public bool InConference;   // one of the loser's states (it may be demanded)
        }

        // per negotiator, as of the ownership and control versions below. Influence used to walk every state of the
        // map and look its owner up for each price: the AI prices every state it holds again after each demand, so
        // a large country's conference took a second and more.
        readonly Dictionary<ushort, List<Reach>> reach = new Dictionary<ushort, List<Reach>>();
        HashSet<int> stateSet;
        int reachOwnership = -1, reachControl = -1;

        /// <summary>The states (with a centre) the negotiator owns or holds, in state order. Ownership and control
        /// by the first land province (states are owned whole at the start; close enough for distances).</summary>
        List<Reach> ReachOf(ushort negotiator, string tag)
        {
            var provinces = gameState.Provinces;
            if (reachOwnership != provinces.OwnershipVersion || reachControl != provinces.ControlVersion)
            {
                reach.Clear();
                reachOwnership = provinces.OwnershipVersion;
                reachControl = provinces.ControlVersion;
            }
            if (reach.TryGetValue(negotiator, out var list)) return list;
            reach[negotiator] = list = new List<Reach>();
            stateSet ??= new HashSet<int>(States);
            Hoi4Occupation.Snapshot(gameState, 0, out var ctrl, out var owner);
            foreach (int s in Hoi4Regions.States)
            {
                ushort first = 0;
                foreach (ushort p in Hoi4Regions.ProvincesOf(s)) if (!Hoi4Regions.IsWater(p)) { first = p; break; }
                if (first == 0 || first >= owner.Length) continue;
                bool owned = owner[first] == negotiator, holds = ctrl[first] == negotiator;
                if (!owned && !holds) continue;
                var info = Hoi4StateData.Get(s);
                if (info == null || !info.HasCenter) continue;
                list.Add(new Reach { State = s, Info = info, Holds = holds, OwnedCore = owned && info.Cores.Contains(tag),
                                     InConference = stateSet.Contains(s) });
            }
            return list;
        }

        static long Dist2(Hoi4StateData.StateInfo a, Hoi4StateData.StateInfo b)
        {
            if (a == null || b == null || !a.HasCenter || !b.HasCenter) return long.MaxValue;
            long dx = a.CenterX - b.CenterX, dy = a.CenterY - b.CenterY;
            return dx * dx + dy * dy;
        }

        /// <summary>0.7 at distance 0 up to 1.0 at the neutral distance and beyond (d = px / 22).</summary>
        static FP Falloff(long dist2, FP neutral)
        {
            if (dist2 == long.MaxValue) return FP.One;
            FP d = FP.Sqrt(FP.FromInt((int)System.Math.Min(dist2, int.MaxValue))) / Divisor;
            return Min + Span * FP.Min(FP.One, d / neutral);
        }

        HashSet<int> Neighbours(int state)
        {
            if (stateNeighbours.TryGetValue(state, out var set)) return set;
            set = new HashSet<int>();
            foreach (ushort p in Hoi4Regions.ProvincesOf(state))
                foreach (ushort q in gameState.Adjacencies.GetNeighborsWhere(p, x => true))
                {
                    int s = Hoi4Regions.State(q);
                    if (s > 0 && s != state) set.Add(s);
                }
            stateNeighbours[state] = set;
            return set;
        }

        static readonly Dictionary<string, string> ideologies = new Dictionary<string, string>();
        static bool ideologiesLoaded;

        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetCache() { ideologies.Clear(); ideologiesLoaded = false; }

        /// <summary>HOI4 cost modifier "ideology" for take_states (00_generic_peace.txt).</summary>
        FP IdeologyFactor(ushort negotiator)
        {
            switch (Ideology(Tag(negotiator)))
            {
                case "democratic": return FP.FromFraction(13, 10);
                case "fascism": return FP.FromFraction(9, 10);
                default: return FP.One;
            }
        }

        /// <summary>The country's ruling ideology at the start (common/hoi4_country_stats.json5).</summary>
        public static string Ideology(string tag)
        {
            if (!ideologiesLoaded && DataFileResolver.IsInitialized)
            {
                ideologiesLoaded = true;
                string path = DataFileResolver.Resolve("common/hoi4_country_stats.json5");
                if (File.Exists(path))
                    foreach (var p in Json5Loader.LoadJson5File(path).Properties())
                        ideologies[p.Name] = (string)p.Value["ideology"] ?? "neutrality";
            }
            return tag != null && ideologies.TryGetValue(tag, out string i) ? i : "neutrality";
        }

        // --- Demands and conclusion -----------------------------------------------------------------------------

        /// <summary>The cost of every demand on the list, each priced with the taker's earlier demands in the list
        /// (influence from adjacent demands, like taking them over several turns). Unaffordable ones fail Check.</summary>
        public FP Spent(ushort negotiator, List<int> demands)
        {
            var done = new List<int>();
            FP sum = FP.Zero;
            foreach (int s in demands)
            {
                sum += Cost(s, negotiator, done);
                done.Add(s);
            }
            return sum;
        }

        /// <summary>The AI's demands: the states it controls (and no one has demanded), best value for the cost first,
        /// while its score lasts.</summary>
        List<int> AiDemands(Winner w, HashSet<int> taken)
        {
            var mine = new List<int>();
            FP left = w.Score;
            var open = new List<int>();     // the states it could still demand, in state order
            foreach (int s in States) if (!taken.Contains(s) && Controller(s) == w.Country) open.Add(s);
            while (open.Count > 0)
            {
                int best = -1; FP bestRatio = FP.Zero, bestCost = FP.Zero;
                foreach (int s in open)
                {
                    FP cost = Cost(s, w.Country, mine);
                    if (cost > left || cost <= FP.Zero) continue;
                    FP ratio = Value(s) / cost;
                    if (best < 0 || ratio > bestRatio) { best = s; bestRatio = ratio; bestCost = cost; }
                }
                if (best < 0) break;
                mine.Add(best);
                open.Remove(best);
                left -= bestCost;
            }
            return mine;
        }

        /// <summary>The player's demands (state, taker) checked against their score, then the AI winners', then
        /// the transfer and the peace.</summary>
        public void Conclude(List<(int state, ushort taker)> playerDemands)
        {
            var takers = new Dictionary<int, ushort>();
            ushort player = StarterKit.Initializer.Instance?.PlayerState?.PlayerCountryId ?? 0;
            var playerStates = new List<int>();
            foreach (var (s, t) in playerDemands)
                if (States.Contains(s) && !takers.ContainsKey(s) && t == player) { takers[s] = t; playerStates.Add(s); }
            var pw = WinnerOf(player);
            if (pw != null && Spent(player, playerStates) > pw.Score + FP.One) // over budget: refused (the UI prevents it)
            {
                ArchonLogger.LogWarning("Hoi4PeaceConference: the player's demands cost more than their score; ignored", "game_war");
                takers.Clear();
            }
            var taken = new HashSet<int>(takers.Keys);
            foreach (var w in Winners)
            {
                if (w.Country == player) continue;
                foreach (int s in AiDemands(w, taken)) { takers[s] = w.Country; taken.Add(s); }
            }
            Apply(takers);
        }

        void Apply(Dictionary<int, ushort> takers)
        {
            var provinces = gameState.Provinces;
            var summary = new List<string>();
            var sorted = new List<int>(takers.Keys);
            sorted.Sort();
            foreach (int s in sorted)
            {
                ushort taker = takers[s];
                foreach (ushort p in Hoi4Regions.ProvincesOf(s))
                {
                    if (provinces.GetProvinceOwner(p) != Loser) continue;
                    provinces.SetProvinceOwner(p, taker);
                    provinces.SetProvinceController(p, taker);
                }
                summary.Add($"{s}->{Tag(taker)}");
            }
            int tick = (int)gameState.Time.CurrentTick;
            foreach (var w in Winners)
                if (gameState.Diplomacy.IsAtWar(Loser, w.Country)) gameState.Diplomacy.MakePeace(Loser, w.Country, tick);
            // Occupations end: land held by a country not at war with its owner goes back to the owner (the
            // loser's remaining land, the winners' land it held, and a third country's land a winner took from it)
            foreach (int s in Hoi4Regions.States)
                foreach (ushort p in Hoi4Regions.ProvincesOf(s))
                {
                    ushort owner = provinces.GetProvinceOwner(p), controller = provinces.GetProvinceController(p);
                    if (owner == 0 || controller == 0 || controller == owner) continue;
                    if (!gameState.Diplomacy.IsAtWar(owner, controller)) provinces.SetProvinceController(p, owner);
                }
            var changed = new HashSet<ushort>();
            foreach (int s in sorted) foreach (ushort p in Hoi4Regions.ProvincesOf(s)) changed.Add(p);
            int sent = SendHome(changed);
            Core.Recording.SessionRecorder.Note("war", "peace", Loser, 0, 0, 0,
                $"{takers.Count} of {States.Count} states taken: {string.Join(" ", summary)}; {sent} divisions sent home");
        }

        /// <summary>
        /// HOI4 after a peace: a division left on the loser's remaining land, or on land the peace gave to another
        /// country, that its country may not be on (not its own, an ally's, an enemy's,
        /// nor with military access; Hoi4MovementRules) can't get out, so it goes to its capital (or, without the
        /// capital, its lowest-id province). Units in id order.
        /// </summary>
        int SendHome(HashSet<ushort> changed)
        {
            var units = gameState.Units;
            var rules = new Hoi4MovementRules(gameState);
            var all = new List<ushort>();
            if (Hoi4Divisions.Instance != null) all.AddRange(Hoi4Divisions.Instance.All.Keys);
            all.Sort();
            var home = new Dictionary<ushort, ushort>();
            int sent = 0;
            foreach (ushort u in all)
            {
                if (!units.HasUnit(u)) continue;
                var unit = units.GetUnit(u);
                ushort owner = gameState.Provinces.GetProvinceOwner(unit.provinceID);
                // only the loser's remaining land and the land it lost changed hands: elsewhere divisions stay (e.g.
                // garrisons stationed abroad at the start, whose access, colonies or puppets, isn't modelled)
                if ((owner != Loser && !changed.Contains(unit.provinceID)) || unit.countryID == Loser || unit.countryID == owner) continue;
                if (rules.HasAccess(unit.countryID, owner)) continue;
                if (!home.TryGetValue(unit.countryID, out ushort to)) home[unit.countryID] = to = HomeOf(unit.countryID);
                if (to == 0) continue;
                units.MovementQueue.CancelMovement(u);
                units.MoveUnit(u, to);
                sent++;
                Core.Recording.SessionRecorder.Note("war", "sent_home", unit.countryID, u, to, unit.provinceID, "no access after the peace");
            }
            return sent;
        }

        ushort HomeOf(ushort country)
        {
            ushort capital = Hoi4Regions.Capital(Tag(country));
            if (capital != 0 && gameState.Provinces.GetProvinceOwner(capital) == country) return capital;
            foreach (int s in Hoi4Regions.States)
                foreach (ushort p in Hoi4Regions.ProvincesOf(s))
                    if (gameState.Provinces.GetProvinceOwner(p) == country && !Hoi4Regions.IsWater(p)) return p;
            return 0;
        }
    }
}
