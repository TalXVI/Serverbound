using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace Serverbound.Compatibility
{
    internal static class OwnerSkillPatch
    {
        internal const string Owner = Plugin.PluginGUID + ".OwnerSkills";
        private const string GrantRpc = "Serverbound.OwnerSkillXP";
        private const string HelloRpc = "Serverbound.OwnerSkillHello";
        private const string StatusRpc = "Serverbound.OwnerSkillStatus";
        private static readonly int ProtocolKey = "Serverbound.skillProtocol".GetStableHashCode();
        private static readonly int MiningKey = "Serverbound.mining".GetStableHashCode();
        private static readonly int WoodKey = "Serverbound.woodcutting".GetStableHashCode();
        private static readonly int AnimalKey = "Serverbound.animalHandling".GetStableHashCode();
        // Levels feed the vendor's threshold checks; factors derive from them as in Skills.GetSkillFactor.
        private static readonly int FarmingLevelKey = "Serverbound.level.farming".GetStableHashCode();
        private static readonly int AnimalLevelKey = "Serverbound.level.animalHandling".GetStableHashCode();
        private static readonly int VoyagingLevelKey = "Serverbound.level.voyaging".GetStableHashCode();
        private const float FlagRange = 30;
        private static readonly HashSet<(ZDOID, int)> minedAreas = new HashSet<(ZDOID, int)>();
        private static readonly HashSet<ZDOID> missingPlayers = new HashSet<ZDOID>();
        private static readonly ConditionalWeakTable<ZRoutedRpc, object> registrations = new ConditionalWeakTable<ZRoutedRpc, object>();
        private static Assembly? impact;
        private static bool prepared, server, installed, coreOnServer;
        private static float nextPublish;
        private static float nextHello;
        private static FieldInfo view = null!, animalSkill = null!, animalRange = null!, animalEnabled = null!, miningEnabled = null!, woodEnabled = null!, voyagingSkill = null!;
        private static MethodInfo treeBonus = null!;
        private static MethodInfo? bonusFactorRead;
        private static Func<float, ConfigEntry<bool>, float>? capFactor;
        [ThreadStatic] private static Player? current;
        
        private static readonly FieldInfo shipPlayers = AccessTools.Field(typeof(Ship), "m_players");

        internal sealed class Scope : IDisposable
        {
            private readonly Player? previous;
            internal Scope(Player? player) { previous = current; current = player; }
            public void Dispose() { current = previous; }
        }

        internal static bool Enabled => server && installed && SimulationPatch.Active;

        internal static void Prepare(Assembly? skills)
        {
            prepared = false; installed = false; impact = null;
            bonusFactorRead = null; capFactor = null;
            if (skills == null)
            {
                CompatibilityInstaller.Info("OwnerSkills: optional mod absent; inactive.");
                return;
            }
            Guard.Build(skills, ExpectedBuilds.ImpactfulSkills);
            try { Guard.Build(typeof(ZNet).Assembly, ExpectedBuilds.Valheim); server = false; }
            catch (NotSupportedException) { Guard.Build(typeof(ZNet).Assembly, ExpectedBuilds.Server); server = true; }
            if (server && !SimulationPatch.CoreActive())
            {
                CompatibilityInstaller.Info("OwnerSkills: dedicated Core absent; inactive on server.");
                return;
            }
            impact = skills;
            view = AccessTools.Field(typeof(Character), "m_nview");
            animalSkill = AccessTools.Field(Guard.Type(skills, "ImpactfulSkills.patches.AnimalWhisper"), "AnimalHandling");
            animalRange = AccessTools.Field(Guard.Type(skills, "ImpactfulSkills.ValConfig"), "AnimalHandlingLootRange");
            animalEnabled = AccessTools.Field(Guard.Type(skills, "ImpactfulSkills.ValConfig"), "EnableAnimalWhisper");
            miningEnabled = AccessTools.Field(Guard.Type(skills, "ImpactfulSkills.ValConfig"), "EnableMining");
            woodEnabled = AccessTools.Field(Guard.Type(skills, "ImpactfulSkills.ValConfig"), "EnableWoodcutting");
            voyagingSkill = AccessTools.Field(Guard.Type(skills, "ImpactfulSkills.patches.Voyaging"), "VoyagingSkill");
            treeBonus = Guard.Method(Guard.Type(skills, "ImpactfulSkills.patches.Woodcutting"), "IncreaseTreeDrops", typeof(void), typeof(TreeBase));
            Type? caps = skills.GetType("ImpactfulSkills.SkillCaps");
            if (caps != null)
            {
                bonusFactorRead = Guard.Method(caps, "GetBonusSkillFactor", typeof(float), typeof(Character), typeof(Skills.SkillType), typeof(ConfigEntry<bool>));
                MethodInfo cap = Guard.Method(caps, "BonusFactor", typeof(float), typeof(float), typeof(ConfigEntry<bool>));
                capFactor = (Func<float, ConfigEntry<bool>, float>)Delegate.CreateDelegate(typeof(Func<float, ConfigEntry<bool>, float>), cap);
            }
            var harmony = new Harmony(Owner);
            harmony.Patch(Guard.Method(typeof(Player), "SetLocalPlayer", typeof(void)), postfix: Guard.Hook(typeof(OwnerSkillPatch), nameof(Publish)));
            harmony.Patch(Guard.Method(typeof(Player), "FixedUpdate", typeof(void)), postfix: Guard.Hook(typeof(OwnerSkillPatch), nameof(PublishPeriodic)));
            foreach (string name in new[] { "RaiseSkill", "LowerAllSkills", "Load", "CheatRaiseSkill" })
                harmony.Patch(AccessTools.DeclaredMethod(typeof(Skills), name), postfix: Guard.Hook(typeof(OwnerSkillPatch), nameof(PublishAfterSkillChange)));
            harmony.Patch(AccessTools.Constructor(typeof(ZRoutedRpc), new[] { typeof(bool) }), postfix: Guard.Hook(typeof(OwnerSkillPatch), nameof(RegisterRpc)));
            harmony.Patch(AccessTools.DeclaredMethod(typeof(ZNet), "Shutdown"), postfix: Guard.Hook(typeof(OwnerSkillPatch), nameof(Reset)));
            MethodInfo miningHook = AccessTools.DeclaredMethod(Guard.Type(skills, "ImpactfulSkills.patches.Mining+Minerock5DestroyPatch"), "Postfix");
            harmony.Patch(miningHook, prefix: Guard.Hook(typeof(OwnerSkillPatch), nameof(OncePerMiningArea)));
            prepared = true;
            CompatibilityInstaller.Info("OwnerSkills: state publisher and XP RPC registered; server integration awaits Core verification.");
        }

        internal static void Verify()
        {
            if (!prepared || !server || !SimulationPatch.Active || impact == null) return;
            prepared = false;
            var removed = new List<(MethodBase Target, Patch Hook, HarmonyPatchType Kind)>();
            try
            {
                var harmony = new Harmony(Owner);
                foreach ((Type type, string name, Type[] args) in new[]
                {
                    (typeof(MineRock), "RPC_Hit", new[] { typeof(long), typeof(HitData), typeof(int) }),
                    (typeof(MineRock5), "RPC_Damage", new[] { typeof(long), typeof(HitData), typeof(int) }),
                    (typeof(TreeBase), "RPC_Damage", new[] { typeof(long), typeof(HitData) }),
                    (typeof(TreeLog), "RPC_Damage", new[] { typeof(long), typeof(HitData) }),
                    (typeof(Destructible), "RPC_Damage", new[] { typeof(long), typeof(HitData) })
                })
                {
                    MethodInfo method = Guard.Method(type, name, typeof(void), args);
                    harmony.Patch(method, prefix: Guard.Hook(typeof(OwnerSkillPatch), nameof(BeginDamage), Priority.First),
                        finalizer: Guard.Hook(typeof(OwnerSkillPatch), nameof(EndScope)));
                }
                foreach ((Type type, string name, string begin) in new[]
                {
                    (typeof(Tameable), "DecreaseRemainingTime", nameof(BeginTaming)),
                    (typeof(CharacterDrop), "GenerateDropList", nameof(BeginLoot)),
                    (typeof(Tameable), "OnDeath", nameof(BeginSlaughter))
                }) harmony.Patch(AccessTools.DeclaredMethod(type, name), prefix: Guard.Hook(typeof(OwnerSkillPatch), begin, Priority.First),
                    finalizer: Guard.Hook(typeof(OwnerSkillPatch), nameof(EndScope)));
                foreach ((MethodInfo method, string begin) in new[]
                {
                    (Guard.Method(typeof(Beehive), "CheckBiome", typeof(bool)), nameof(BeginHive)),
                    (Guard.Method(typeof(Plant), "UpdateHealth", typeof(void), typeof(double)), nameof(BeginPlant)),
                    (Guard.Method(typeof(WearNTear), "RPC_Damage", typeof(void), typeof(long), typeof(HitData)), nameof(BeginShipDamage)),
                    (AccessTools.DeclaredMethod(typeof(ImpactEffect), "OnCollisionEnter"), nameof(BeginShipImpact))
                }) harmony.Patch(method, prefix: Guard.Hook(typeof(OwnerSkillPatch), begin, Priority.First),
                    finalizer: Guard.Hook(typeof(OwnerSkillPatch), nameof(EndScope)));

                foreach (string name in new[]
                {
                    "ImpactfulSkills.patches.Mining+MinerockDropsPatch",
                    "ImpactfulSkills.patches.Mining+Minerock5DestroyPatch",
                    "ImpactfulSkills.patches.Mining+IncreaseDropsFromDestructibleRock",
                    "ImpactfulSkills.patches.Woodcutting+IncreaseDropsFromTree",
                    "ImpactfulSkills.patches.Woodcutting+IncreaseDropsFromDestructibleTree",
                    "ImpactfulSkills.patches.AnimalWhisper+IncreaseTamingSpeed",
                    "ImpactfulSkills.patches.AnimalWhisper+ScaleTamedAnimalLoot",
                    "ImpactfulSkills.patches.AnimalWhisper+TamedAnimalSlaughterXP"
                })
                {
                    Type type = Guard.Type(impact, name);
                    MethodInfo method = AccessTools.DeclaredMethod(type, name.Contains("MinerockDropsPatch") ? "ApplyIncreasedMiningDrops"
                        : name.Contains("IncreaseDropsFromTree") || name.Contains("Minerock5DestroyPatch")
                          || name.Contains("ScaleTamedAnimalLoot") || name.Contains("TamedAnimalSlaughterXP") ? "Postfix" : "Prefix");
                    harmony.Patch(method, transpiler: Guard.Hook(typeof(OwnerSkillPatch), nameof(RewriteContext)));
                }
                foreach (string type in new[] { "Mining", "Woodcutting" })
                    harmony.Patch(AccessTools.DeclaredMethod(Guard.Type(impact, "ImpactfulSkills.patches." + type),
                        type == "Mining" ? "IncreaseMiningDrops" : "IncreaseWoodDrops"),
                        transpiler: Guard.Hook(typeof(OwnerSkillPatch), nameof(RewriteContext)));
                foreach ((string type, string name) in new[]
                {
                    ("AnimalWhisper+BehivesInAnyBiome", "Prefix"),
                    ("PlantBiome", "IsBiomeUnrestricted"),
                    ("Voyaging+ShipDamageReduction", "Prefix"),
                    ("Voyaging+ShipDamageImpactReduction", "ImpactDamagesSelf")
                }) harmony.Patch(AccessTools.DeclaredMethod(Guard.Type(impact, "ImpactfulSkills.patches." + type), name),
                    transpiler: Guard.Hook(typeof(OwnerSkillPatch), nameof(RewriteContext)));
                harmony.Patch(treeBonus, prefix: Guard.Hook(typeof(OwnerSkillPatch), nameof(HasTreeActor)));

                var tree = SimulationPatch.ExactHook(typeof(TreeBase), "RPC_Damage",
                    AccessTools.DeclaredMethod(Guard.Type(impact, "ImpactfulSkills.patches.Woodcutting+DamageHandler_Apply_Patch"), "Transpiler"),
                    "MidnightsFX.ImpactfulSkills", HarmonyPatchType.Transpiler);
                // Validate the unmodified body before removing any vendor registration.
                FixTreeAnchor(PatchProcessor.GetOriginalInstructions(tree.Target)).ToArray();
                removed.Add(tree); harmony.Unpatch(tree.Target, tree.Hook.PatchMethod);
                harmony.Patch(tree.Target, transpiler: Guard.Hook(typeof(OwnerSkillPatch), nameof(FixTreeAnchor)));
                SimulationPatch.RememberRemoved(tree);
                installed = true;
                CompatibilityInstaller.Info("OwnerSkills: APPLIED; owner-side calculations use replicated actor skills; original formulas and server ownership retained.");
            }
            catch (Exception error)
            {
                installed = false;
                try
                {
                    new Harmony(Owner).UnpatchSelf();
                    foreach (var hook in removed) SimulationPatch.Restore(hook);
                }
                catch (Exception rollback)
                {
                    CompatibilityInstaller.Error("OwnerSkills: rollback failed; inspect Harmony state before launch. " + rollback);
                }
                CompatibilityInstaller.Error("OwnerSkills: NOT APPLIED; required launch gate failed. " + error);
            }
        }

        private static Skills.SkillType Animal => (Skills.SkillType)animalSkill.GetValue(null);
        private static Skills.SkillType Voyaging => (Skills.SkillType)voyagingSkill.GetValue(null);
        private static float LootRange => ((ConfigEntry<float>)animalRange.GetValue(null)).Value;
        private static bool Configured(FieldInfo field) => ((ConfigEntry<bool>)field.GetValue(null)).Value;
        private static ZDO? PlayerZdo(Player player) => (view.GetValue(player) as ZNetView)?.GetZDO();
        private static int Key(Skills.SkillType skill) => skill == Skills.SkillType.Pickaxes ? MiningKey
            : skill == Skills.SkillType.WoodCutting ? WoodKey : skill == Animal ? AnimalKey
            : throw new NotSupportedException("Unexpected owner-side skill " + skill);
        private static int LevelKey(Skills.SkillType skill) => skill == Skills.SkillType.Farming ? FarmingLevelKey
            : skill == Animal ? AnimalLevelKey : skill == Voyaging ? VoyagingLevelKey
            : throw new NotSupportedException("Unexpected owner-side skill level " + skill);

        private static void Publish(Player __instance)
        {
            if (server || !coreOnServer || __instance != Player.m_localPlayer) return;
            ZDO? zdo = PlayerZdo(__instance);
            if (zdo == null || zdo.GetOwner() != ZNet.GetUID()) return;
            foreach (var skill in new[] { Skills.SkillType.Pickaxes, Skills.SkillType.WoodCutting, Animal })
            {
                float factor = __instance.GetSkillFactor(skill);
                if (!SkillState.ValidFactor(factor)) throw new InvalidOperationException("Invalid local skill factor.");
                zdo.Set(Key(skill), factor);
            }
            foreach (var skill in new[] { Skills.SkillType.Farming, Animal, Voyaging })
            {
                float level = __instance.GetSkillLevel(skill);
                if (!SkillState.ValidLevel(level)) throw new InvalidOperationException("Invalid local skill level.");
                zdo.Set(LevelKey(skill), level);
            }
            zdo.Set(ProtocolKey, SkillState.Protocol);
        }

        private static void PublishPeriodic(Player __instance)
        {
            if (server || __instance != Player.m_localPlayer || Time.unscaledTime < nextPublish) return;
            if (ZRoutedRpc.instance != null && Time.unscaledTime >= nextHello)
            {
                nextHello = Time.unscaledTime + 5;
                ZRoutedRpc.instance.InvokeRoutedRPC(ServerId(), HelloRpc);
            }
            nextPublish = Time.unscaledTime + 0.25f; Publish(__instance);
        }

        private static void PublishAfterSkillChange(Skills __instance)
        {
            Player player = Player.m_localPlayer;
            if (!server && player && player.GetSkills() == __instance) Publish(player);
        }

        internal static Player? Actor() => Enabled ? current : Player.m_localPlayer;

        internal static float Factor(Character player, Skills.SkillType skill)
        {
            if (!Enabled) return player.GetSkillFactor(skill);
            if (skill == Voyaging) return Mathf.Clamp01(Level(player, skill) / 100f);
            ZDO? zdo = PlayerZdo((Player)player);
            if (zdo == null) throw new InvalidOperationException("Player has no replicated skill state.");
            return SkillState.RequireFactor(zdo.GetInt(ProtocolKey), zdo.GetFloat(Key(skill), float.NaN));
        }

        internal static float Level(Character player, Skills.SkillType skill)
        {
            if (!Enabled) return player.GetSkillLevel(skill);
            ZDO? zdo = PlayerZdo((Player)player);
            if (zdo == null) throw new InvalidOperationException("Player has no replicated skill state.");
            return SkillState.RequireLevel(zdo.GetInt(ProtocolKey), zdo.GetFloat(LevelKey(skill), float.NaN));
        }

        private static float FactorWithCap(Character player, Skills.SkillType skill, ConfigEntry<bool> pastLevel100)
            => capFactor!(Factor(player, skill), pastLevel100);

        private static void Raise(Character player, Skills.SkillType skill, float amount)
        {
            if (!Enabled) { player.RaiseSkill(skill, amount); return; }
            var package = new ZPackage(); package.Write(player.GetZDOID()); package.Write((int)skill); package.Write(amount);
            ZRoutedRpc.instance.InvokeRoutedRPC(player.GetOwner(), GrantRpc, package);
        }

        private static long ServerId() => ZRoutedRpc.instance.GetServerPeerID();
        private static void RegisterRpc(ZRoutedRpc __instance)
        {
            if (registrations.TryGetValue(__instance, out _)) return;
            __instance.Register<ZPackage>(GrantRpc, GrantXP);
            __instance.Register(HelloRpc, Hello);
            __instance.Register<bool>(StatusRpc, Status);
            registrations.Add(__instance, new object());
        }
        private static void Hello(long sender)
        {
            if (server && sender != 0 && ZRoutedRpc.instance.m_peers.Any(p => p.m_uid == sender && p.IsReady()))
                ZRoutedRpc.instance.InvokeRoutedRPC(sender, StatusRpc, Enabled);
        }
        private static void Status(long sender, bool active)
        {
            if (server || sender == 0 || sender != ServerId()) return;
            coreOnServer = active;
            if (active && Player.m_localPlayer) Publish(Player.m_localPlayer);
        }
        private static void GrantXP(long sender, ZPackage package)
        {
            if (server || !coreOnServer || ZRoutedRpc.instance == null || sender == 0 || sender != ServerId()) return;
            ZDOID id = package.ReadZDOID(); int skill = package.ReadInt(); float amount = package.ReadSingle();
            Player player = Player.m_localPlayer;
            if (player && id == player.GetZDOID() && skill == (int)Animal && amount >= 0 && !float.IsNaN(amount) && !float.IsInfinity(amount))
                player.RaiseSkill((Skills.SkillType)skill, amount);
        }

        private static Coroutine RunCoroutine(MonoBehaviour original, IEnumerator routine) => Enabled
            ? Plugin.instance.StartCoroutine(routine) : original.StartCoroutine(routine);

        internal static IEnumerable<CodeInstruction> RewriteContext(IEnumerable<CodeInstruction> instructions)
        {
            FieldInfo local = AccessTools.Field(typeof(Player), "m_localPlayer");
            MethodInfo factor = AccessTools.Method(typeof(Character), "GetSkillFactor");
            MethodInfo level = AccessTools.Method(typeof(Character), "GetSkillLevel");
            MethodInfo raise = AccessTools.Method(typeof(Character), "RaiseSkill");
            MethodInfo coroutine = AccessTools.Method(typeof(MonoBehaviour), "StartCoroutine", new[] { typeof(IEnumerator) });
            foreach (CodeInstruction source in instructions)
            {
                var code = new CodeInstruction(source);
                if (code.opcode == OpCodes.Ldsfld && Equals(code.operand, local))
                { code.opcode = OpCodes.Call; code.operand = AccessTools.Method(typeof(OwnerSkillPatch), nameof(Actor)); }
                else if (code.Calls(factor)) { code.opcode = OpCodes.Call; code.operand = AccessTools.Method(typeof(OwnerSkillPatch), nameof(Factor)); }
                else if (bonusFactorRead != null && code.Calls(bonusFactorRead))
                { code.opcode = OpCodes.Call; code.operand = AccessTools.Method(typeof(OwnerSkillPatch), nameof(FactorWithCap)); }
                else if (code.Calls(level)) { code.opcode = OpCodes.Call; code.operand = AccessTools.Method(typeof(OwnerSkillPatch), nameof(Level)); }
                else if (code.Calls(raise)) { code.opcode = OpCodes.Call; code.operand = AccessTools.Method(typeof(OwnerSkillPatch), nameof(Raise)); }
                else if (code.Calls(coroutine)) { code.opcode = OpCodes.Call; code.operand = AccessTools.Method(typeof(OwnerSkillPatch), nameof(RunCoroutine)); }
                yield return code;
            }
        }

        // Missing skill state runs the action without an actor: vanilla results, no ImpactfulSkills bonus.
        private static void BeginDamage(Component __instance, HitData __1, out Scope? __state)
        {
            __state = null;
            if (!Enabled) return;
            Player? player = __1?.GetAttacker() as Player;
            bool mining = __instance is MineRock || __instance is MineRock5;
            bool wood = __instance is TreeBase || __instance is TreeLog;
            if (__instance is Destructible)
            {
                int kind = (int)AccessTools.Field(typeof(Destructible), "m_destructibleType").GetValue(__instance);
                mining = kind == 1; wood = kind == 2;
            }
            if (player != null && ((mining && Configured(miningEnabled) && !Ready(player, Skills.SkillType.Pickaxes))
                || (wood && Configured(woodEnabled) && !Ready(player, Skills.SkillType.WoodCutting)))) player = null;
            __state = new Scope(player);
        }

        private static bool Ready(Player player, Skills.SkillType skill)
        {
            try
            {
                Factor(player, skill);
                missingPlayers.Remove(player.GetZDOID()); return true;
            }
            catch (InvalidOperationException)
            {
                if (missingPlayers.Add(player.GetZDOID()))
                    CompatibilityInstaller.Warning("OwnerSkills: no valid skill state from player " + player.GetZDOID()
                        + "; their owner-side ImpactfulSkills bonuses are skipped. Check the client's matching compatibility package.");
                return false;
            }
        }

        private static Player? Nearby(Component target, float range) => SkillState.Closest(Player.GetAllPlayers(),
            p => (p.transform.position - target.transform.position).sqrMagnitude, p => p.GetOwner(), range);
        private static void BeginTaming(Tameable __instance, out Scope? __state)
        {
            __state = null; if (!Enabled || !Configured(animalEnabled)) return;
            Player? player = Nearby(__instance, 30);
            __state = new Scope(player != null && Ready(player, Animal) ? player : null);
        }
        private static void BeginLoot(CharacterDrop __instance, out Scope? __state)
        {
            __state = null;
            if (!Enabled || !Configured(animalEnabled)) return;
            Character character = __instance.GetComponent<Character>();
            Player? player = character && character.IsTamed() ? Nearby(__instance, LootRange) : null;
            __state = new Scope(player != null && Ready(player, Animal) ? player : null);
        }
        private static void BeginSlaughter(Tameable __instance, out Scope? __state)
        {
            __state = null;
            if (!Enabled || !Configured(animalEnabled)) return;
            Character character = __instance.GetComponent<Character>();
            Player? player = character && character.IsTamed() ? Nearby(__instance, LootRange) : null;
            __state = new Scope(player != null && Ready(player, Animal) ? player : null);
        }
        private static void EndScope(Scope? __state) => __state?.Dispose();
        private static bool HasTreeActor() => !Enabled || current != null;

        // The vendor checks the owner's local player. A server owner uses the most skilled qualifying player instead.
        private static bool HasLevel(Player player, Skills.SkillType skill)
        {
            try { Level(player, skill); return true; }
            catch (InvalidOperationException) { return false; }
        }
        private static Player? Best(IEnumerable<Player> players, Skills.SkillType skill) => players.Where(p => p && HasLevel(p, skill))
            .OrderByDescending(p => Level(p, skill)).ThenBy(p => p.GetOwner()).FirstOrDefault();
        private static IEnumerable<Player> Near(Component target, float range) => Player.GetAllPlayers()
            .Where(p => (p.transform.position - target.transform.position).sqrMagnitude <= range * range);
        private static void BeginHive(Beehive __instance, out Scope? __state) =>
            __state = Enabled ? new Scope(Best(Near(__instance, FlagRange), Animal)) : null;
        private static void BeginPlant(Plant __instance, out Scope? __state) =>
            __state = Enabled ? new Scope(Best(Near(__instance, FlagRange), Skills.SkillType.Farming)) : null;
        private static void BeginShipDamage(WearNTear __instance, out Scope? __state) =>
            __state = Enabled ? new Scope(Aboard(__instance.GetComponent<Ship>())) : null;
        private static void BeginShipImpact(ImpactEffect __instance, out Scope? __state) =>
            __state = Enabled ? new Scope(Aboard(__instance.GetComponent<Ship>())) : null;
        private static Player? Aboard(Ship? ship) => ship ? Best((List<Player>)shipPlayers.GetValue(ship), Voyaging) : null;
        private static bool OncePerMiningArea(MineRock5 __0, int __2, float __3)
        {
            if ((!server && !coreOnServer) || (server && !Enabled)) return true;
            ZNetView? nview = AccessTools.Field(typeof(MineRock5), "m_nview").GetValue(__0) as ZNetView;
            ZDO? zdo = nview?.GetZDO();
            if (zdo == null || zdo.GetOwner() != ServerId()) return true;
            // SetAreaHealth is broadcast. Clients must not each spawn the owner's bonus.
            if (!server) return false;
            return __3 <= 0 && current != null && minedAreas.Add((zdo.m_uid, __2));
        }
        private static void TreeDrops(TreeBase tree) => treeBonus.Invoke(null, new object[] { tree });

        internal static IEnumerable<CodeInstruction> FixTreeAnchor(IEnumerable<CodeInstruction> instructions)
        {
            var codes = instructions.Select(c => new CodeInstruction(c)).ToList();
            if (codes.Count < 3) throw new NotSupportedException("TreeBase damage body changed; refusing to move drop logic.");
            var matches = Enumerable.Range(0, codes.Count - 2).Where(i => codes[i].opcode == OpCodes.Ldloc_0
                && codes[i + 1].opcode == OpCodes.Ldc_R4 && Equals(codes[i + 1].operand, 0f)
                && (codes[i + 2].opcode == OpCodes.Bgt_Un || codes[i + 2].opcode == OpCodes.Bgt_Un_S)).ToArray();
            if (matches.Length != 2) throw new NotSupportedException("TreeBase damage health branches changed; refusing to move drop logic.");
            codes.InsertRange(matches[1] + 3, new[] { new CodeInstruction(OpCodes.Ldarg_0),
                CodeInstruction.Call(typeof(OwnerSkillPatch), nameof(TreeDrops)) });
            return codes;
        }

        private static void Reset() { minedAreas.Clear(); missingPlayers.Clear(); nextPublish = 0; nextHello = 0; current = null; coreOnServer = false; }
    }
}
