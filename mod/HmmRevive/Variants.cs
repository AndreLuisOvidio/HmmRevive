using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HeavyMetalMachines;
using HeavyMetalMachines.Combat;
using HeavyMetalMachines.Combat.Gadget;
using HeavyMetalMachines.Combat.GadgetScript;
using HeavyMetalMachines.Match;
using CharacterInfo = HeavyMetalMachines.Characters.CharacterInfo;
using Object = UnityEngine.Object;

namespace HmmRevive
{
    /// <summary>
    /// Mod cars built from the game's own: a base car with gadgets taken from other cars. A variant has its own car number
    /// (100+), picked like any car (--hmmrevive-car, bot cars, "/car"). For the game it stays the base car (same item,
    /// portrait, skins, role and bot goals); only its CharacterInfo is a clone of the base's with some gadget slots
    /// replaced, and CarSwap.Rebuild puts it on the player's car: at the first countdown for a launch pick, or mid-match.
    /// Server and clients build the same clone from their own preloaded cars (CarSwap preload).
    /// </summary>
    public static class Variants
    {
        public sealed class Variant
        {
            public int Id;            // car number for the launcher, the chat and --hmmrevive-car
            public string Name;       // shown in the chat and the launcher
            public string[] Aliases;  // also accepted by name (exact match, ignoring case, spaces, - and _)
            public int Base;          // the car it is for the game
            public int BoostFrom;     // car whose boost (E, GadgetSlot.BoostGadget) it gets
            public float Cooldown;    // seconds for every ability (weapons 1-2, special, boost); 0 = each gadget's own
        }

        public static readonly Variant[] All =
        {
            new Variant { Id = 101, Name = "Stingray W", Aliases = new[] { "stingrayw", "stingray+wildfire" }, Base = 1, BoostFrom = 6 },
            new Variant { Id = 102, Name = "Stingray W CD", Aliases = new[] { "stingraywcd" }, Base = 1, BoostFrom = 6, Cooldown = 0.1f },
        };

        private static readonly Dictionary<int, CharacterInfo> Built = new Dictionary<int, CharacterInfo>();
        private static readonly Dictionary<CharacterInfo, int> IdByInfo = new Dictionary<CharacterInfo, int>();
        // Cooldown override (seconds) by car object id from a variant (ApplyGadgetOverrides), for every gadget kind.
        private static readonly Dictionary<int, float> CooldownByCar = new Dictionary<int, float>();
        private static readonly string[] AbilitySlots = { "CustomGadget0", "CustomGadget1", "CustomGadget2", "BoostGadget" };

        public static Variant Get(int id) => All.FirstOrDefault(v => v.Id == id);

        /// <summary>The game's car for a car number: the base of a variant, else the number itself.</summary>
        public static int Base(int id) => Get(id)?.Base ?? id;

        /// <summary>Variant number for a number or name typed by a player, or -1 (then it's a game car, or nothing).</summary>
        public static int Resolve(string want)
        {
            if (string.IsNullOrEmpty(want)) return -1;
            int id;
            if (int.TryParse(want.Trim(), out id)) return Get(id) != null ? id : -1;
            string key = Normalize(want);
            Variant v = All.FirstOrDefault(x => Normalize(x.Name) == key || x.Aliases.Any(a => Normalize(a) == key));
            return v != null ? v.Id : -1;
        }

        /// <summary>The car a player drives as the mod sees it: a variant's number, else the game's car.</summary>
        public static int Current(PlayerData player)
        {
            int id;
            return player.Character != null && IdByInfo.TryGetValue(player.Character, out id) ? id : player.CharacterId;
        }

        /// <summary>The variant's CharacterInfo, built once from the loaded cars; null while its cars aren't loaded.</summary>
        public static CharacterInfo Info(int id, Dictionary<int, CharacterInfo> loaded)
        {
            Variant v = Get(id);
            if (v == null) return null;
            CharacterInfo built;
            if (Built.TryGetValue(id, out built) && built != null) return built;
            CharacterInfo baseInfo, donor;
            if (!loaded.TryGetValue(v.Base, out baseInfo) || baseInfo == null || !loaded.TryGetValue(v.BoostFrom, out donor) || donor == null)
            {
                Log.Error($"variant {v.Name}: car {v.Base} or {v.BoostFrom} isn't loaded");
                return null;
            }
            built = Object.Instantiate(baseInfo);
            built.name = baseInfo.name + "+" + donor.name + "_Boost";
            built.hideFlags = UnityEngine.HideFlags.DontUnloadUnusedAsset; // only the mod references it
            // Not serialized by Unity: OnAfterDeserialize derives it from _characterItemType, but copy it to be sure.
            FieldInfo guid = typeof(CharacterInfo).GetField("_characterItemTypeGuid", BindingFlags.Instance | BindingFlags.NonPublic);
            if (guid != null) guid.SetValue(built, guid.GetValue(baseInfo));

            // The boost exists in two forms: a GadgetInfo in the BoostGadget field (older cars) or a scripted CombatGadget in
            // CustomGadgets with Slot = BoostGadget (newer ones). Take the donor's in whichever form it has.
            built.BoostGadget = donor.BoostGadget;
            var custom = (baseInfo.CustomGadgets ?? new CombatGadget[0]).Where(g => g != null && g.Slot != GadgetSlot.BoostGadget).ToList();
            custom.AddRange((donor.CustomGadgets ?? new CombatGadget[0]).Where(g => g != null && g.Slot == GadgetSlot.BoostGadget));
            built.CustomGadgets = custom.ToArray();

            Built[id] = built;
            IdByInfo[built] = id;
            Log.Info($"variant {v.Id} {v.Name} built: {baseInfo.name} with the boost of {donor.name}");
            Describe(baseInfo);
            Describe(donor);
            Describe(built);
            return built;
        }

        /// <summary>
        /// After CarSwap.Rebuild created a car's gadgets (server and clients): a variant's own cooldown, if it has one.
        /// Classic gadgets (GadgetBehaviour) keep theirs per car in _cooldown, set from GadgetInfo.Cooldown in SetInfo; it's
        /// changed there and not in the GadgetInfo, which every car of that kind shares. Scripted gadgets (CombatGadget)
        /// read theirs in StartCooldownBlock.GetCooldown, which asks Cooldowns.ScriptCooldown. The game's cooldown reductions still
        /// apply on top, as they do to the gadgets' own values.
        /// </summary>
        public static void ApplyGadgetOverrides(CombatObject combat, CharacterInfo info)
        {
            int id;
            Variant v = info != null && IdByInfo.TryGetValue(info, out id) ? Get(id) : null;
            int car = combat.Id.ObjId;
            if (v == null || v.Cooldown <= 0f)
            {
                CooldownByCar.Remove(car);
                return;
            }
            CooldownByCar[car] = v.Cooldown;
            FieldInfo cooldownField = typeof(GadgetBehaviour).GetField("_cooldown", BindingFlags.Instance | BindingFlags.NonPublic);
            var changed = new List<string>();
            foreach (string slot in AbilitySlots)
            {
                var gadget = typeof(CombatObject).GetField(slot).GetValue(combat) as GadgetBehaviour;
                var cooldown = gadget != null ? cooldownField.GetValue(gadget) as Upgradeable : null;
                if (cooldown == null) continue;
                changed.Add($"{slot} {cooldown.Get():0.##}s");
                cooldown.Value = v.Cooldown;
                cooldown.Level = 0; // Get() returns Value at level 0
            }
            Log.Info($"variant {v.Name}: cooldowns {v.Cooldown}s (car {car}; was {string.Join(", ", changed.ToArray())}; scripted gadgets too)");
        }

        /// <summary>A mod car's own cooldown for a car object id (it wins over the lobby's, see Cooldowns.cs).</summary>
        public static bool OwnCooldown(int car, out float seconds) => CooldownByCar.TryGetValue(car, out seconds);

        public static bool AnyOwnCooldown => CooldownByCar.Count > 0;

        // Logs a car's gadget slots, so a variant that misbehaves can be traced to what it took from where. A scripted
        // gadget that reads parameters from another slot (_parametersFromOtherGadgets) depends on that slot's gadget.
        private static void Describe(CharacterInfo info)
        {
            try
            {
                var parts = new List<string>();
                foreach (string slot in new[] { "CustomGadget0", "CustomGadget1", "CustomGadget2", "BoostGadget", "PassiveGadget", "GenericGadget" })
                {
                    var g = typeof(CharacterInfo).GetField(slot).GetValue(info) as GadgetInfo;
                    if (g != null) parts.Add($"{slot}={g.name}({g.GadgetType()?.Name})");
                }
                foreach (CombatGadget g in info.CustomGadgets ?? new CombatGadget[0])
                {
                    if (g == null) continue;
                    string routes = "";
                    var list = typeof(CombatGadget).GetField("_parametersFromOtherGadgets", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(g) as IList;
                    if (list != null && list.Count > 0)
                        routes = " reads " + string.Join(",", list.Cast<object>().Select(r => r.GetType().GetField("Slot")?.GetValue(r)?.ToString() ?? "?").ToArray());
                    parts.Add($"script {g.Slot}={g.name}{routes}");
                }
                Log.Info($"gadgets of {info.name}: " + string.Join("; ", parts.ToArray()));
            }
            catch (Exception e)
            {
                Log.Error($"gadgets of {info?.name}: {e.Message}");
            }
        }

        private static string Normalize(string s) =>
            s.Replace(" ", "").Replace("_", "").Replace("-", "").Replace("'", "").Replace(".", "").ToLowerInvariant();
    }
}
