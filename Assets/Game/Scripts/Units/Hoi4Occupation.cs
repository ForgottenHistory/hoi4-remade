using System;
using System.Collections.Generic;
using System.Collections;
using Core;
using Core.Systems;
using Core.Units;
using Hoi4.Map;
using UnityEngine;

namespace Hoi4.Units
{
    /// <summary>
    /// GAME: occupation. A division that walks into a province held by a country it is at war with takes
    /// control of it: the province's controller changes, its owner does not (the engine's
    /// SetProvinceController). An owner's division walking back in, or an ally's, takes it back. Retreating
    /// divisions only ever arrive on friendly ground, so they occupy nothing.
    /// The map paints an occupied province in the controller's colour, like HOI4, using
    /// a province-indexed texture (_Hoi4ControllerTex, 0 = not occupied, else the controller) read by
    /// Hoi4MapModes.hlsl.
    /// </summary>
    public sealed class Hoi4Occupation : MonoBehaviour
    {
        const int Width = 256, Rows = 256; // one texel per province ID, up to 65536
        static readonly int TexId = Shader.PropertyToID("_Hoi4ControllerTex");

        GameState gameState;
        Texture2D texture;
        float[] data;
        IDisposable[] subscriptions;
        bool dirty;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Spawn()
        {
            if (!Hoi4.Hoi4Scenes.Active) return; // not in other scenes (the engine's StarterKit scene)
            var go = new GameObject("Hoi4Occupation");
            DontDestroyOnLoad(go);
            go.AddComponent<Hoi4Occupation>();
        }

        /// <summary>Who controls the province: its controller, or its owner when none is recorded.</summary>
        public static ushort ControllerOf(GameState gameState, ushort province)
        {
            ushort controller = gameState.Provinces.GetProvinceController(province);
            return controller != 0 ? controller : gameState.Provinces.GetProvinceOwner(province);
        }

        IEnumerator Start()
        {
            while (Engine.ArchonEngine.Instance == null || Engine.ArchonEngine.Instance.GameState == null
                   || Engine.ArchonEngine.Instance.GameState.Provinces == null
                   || Engine.ArchonEngine.Instance.GameState.Units == null
                   || Engine.ArchonEngine.Instance.GameState.Diplomacy == null)
                yield return null;
            gameState = Engine.ArchonEngine.Instance.GameState;

            texture = new Texture2D(Width, Rows, TextureFormat.RFloat, false, linear: true)
            {
                filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = "Hoi4ControllerTex"
            };
            data = new float[Width * Rows];
            for (int p = 1; p < Width * Rows; p++) Write((ushort)p);
            Shader.SetGlobalTexture(TexId, texture);
            dirty = true;

            subscriptions = new[]
            {
                gameState.EventBus.Subscribe<UnitMovementCompletedEvent>(e => OnArrived(e.UnitID, e.DestinationProvinceID)),
                gameState.EventBus.Subscribe<ProvinceControllerChangedEvent>(e => { Write(e.ProvinceId); dirty = true; FollowLakes(e.ProvinceId); }),
                gameState.EventBus.Subscribe<ProvinceOwnershipChangedEvent>(e => { Write(e.ProvinceId); dirty = true; }),
            };
        }

        void OnDestroy()
        {
            if (subscriptions != null)
                foreach (var s in subscriptions) s?.Dispose();
            if (texture != null) Destroy(texture);
        }

        void LateUpdate()
        {
            if (!dirty || texture == null) return;
            dirty = false;
            texture.SetPixelData(data, 0);
            texture.Apply(false);
        }

        void Write(ushort province)
        {
            ushort owner = gameState.Provinces.GetProvinceOwner(province);
            ushort controller = gameState.Provinces.GetProvinceController(province);
            data[province] = owner != 0 && controller != 0 && controller != owner ? controller : 0f;
        }

        /// <summary>
        /// Units can't enter water, so an owned lake follows the land around it: once every land province
        /// touching it has the same controller, the lake goes to that controller (and back to its owner otherwise).
        /// </summary>
        void FollowLakes(ushort province)
        {
            if (Hoi4Regions.IsWater(province)) return;
            var lakes = new List<ushort>();
            foreach (ushort n in gameState.Adjacencies.GetNeighborsWhere(province, p => Hoi4Regions.IsWater(p) && gameState.Provinces.GetProvinceOwner(p) != 0))
                lakes.Add(n);
            foreach (ushort lake in lakes)
            {
                ushort shared = 0;
                bool unanimous = true;
                foreach (ushort n in gameState.Adjacencies.GetNeighborsWhere(lake, p => !Hoi4Regions.IsWater(p)))
                {
                    ushort c = ControllerOf(gameState, n);
                    if (shared == 0) shared = c;
                    else if (shared != c) { unanimous = false; break; }
                }
                gameState.Provinces.SetProvinceController(lake, unanimous && shared != 0 ? shared : gameState.Provinces.GetProvinceOwner(lake));
            }
        }

        void OnArrived(ushort unitId, ushort province)
        {
            var units = gameState.Units;
            if (!units.HasUnit(unitId)) return;
            var diplomacy = gameState.Diplomacy;
            ushort country = units.GetUnit(unitId).countryID;
            ushort owner = gameState.Provinces.GetProvinceOwner(province);
            ushort controller = ControllerOf(gameState, province);
            if (owner == 0 || controller == country || !diplomacy.IsAtWar(country, controller)) return;

            // Allies liberate land for its owner rather than keeping it
            bool liberates = owner != country && diplomacy.AreAllied(country, owner) && !diplomacy.IsAtWar(owner, country);
            gameState.Provinces.SetProvinceController(province, liberates && diplomacy.IsAtWar(owner, controller) ? owner : country);
        }
    }
}
