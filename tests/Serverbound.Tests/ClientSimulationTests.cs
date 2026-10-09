using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using BepInEx;
using HarmonyLib;
using UnityEngine;

internal static class ClientSimulationTests
{
    private static readonly List<IntPtr> pointers = new List<IntPtr>();
    private static readonly Dictionary<Behaviour, bool> enabled = new Dictionary<Behaviour, bool>();
    private static readonly List<(Skills.SkillType Skill, float Amount)> received = new List<(Skills.SkillType, float)>();
    private static Type skills = null!;
    private static int sequence;
    private static bool SetEnabled(Behaviour __instance, bool __0) { enabled[__instance] = __0; return false; }
    private static bool Skip() => false;
    private static void Noop() { }
    private static bool UID(ref long __result) { __result = 900; return false; }
    private static bool LocalFactor(ref float __result) { __result = 0.75f; return false; }
    private static bool Award(Skills.SkillType __0, float __1) { received.Add((__0, __1)); return false; }
    private static HarmonyMethod Hook(string name) => new HarmonyMethod(AccessTools.Method(typeof(ClientSimulationTests), name));
    private static T Fake<T>() where T : UnityEngine.Object
    {
        var value = (T)FormatterServices.GetUninitializedObject(typeof(T));
        IntPtr pointer = Marshal.AllocHGlobal(sizeof(long)); pointers.Add(pointer); Marshal.WriteInt32(pointer, ++sequence);
        AccessTools.Field(typeof(UnityEngine.Object), "m_CachedPtr").SetValue(value, pointer); return value;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    internal static void Run(string lab, Action<string, Action> test)
    {
        Assembly impact = Assembly.LoadFrom(Directory.GetFiles(Path.Combine(lab, "BepInEx/plugins"), "ImpactfulSkills.dll", SearchOption.AllDirectories).Single());
        skills = typeof(Serverbound.Plugin).Assembly.GetType("Serverbound.Compatibility.OwnerSkillPatch", true)!;
        var info = new List<string>(); var errors = new List<string>();
        void Case(string name, Action action) => test("client simulation " + name, () =>
        {
            Serverbound.Compatibility.CompatibilityInstaller.Install(_ => null, info.Add, info.Add, errors.Add);
            info.Clear(); errors.Clear();
            try { action(); }
            finally
            {
                foreach (string owner in new[] { "akoozie.valheimtune", "Serverbound.Tests.Tune", "Serverbound.Tests.ClientSkills", "org.serverbound.valheim.OwnerSkills" }) new Harmony(owner).UnpatchSelf();
                Player.m_localPlayer = null; enabled.Clear(); received.Clear();
                AccessTools.Method(skills, "Reset").Invoke(null, null);
                foreach (IntPtr pointer in pointers) Marshal.FreeHGlobal(pointer);
                pointers.Clear();
            }
        });
        Case("registers client skill publication without a local simulation mod", () =>
        {
            AccessTools.Method(skills, "Prepare").Invoke(null, new object?[] { impact });
            Check(Harmony.GetPatchInfo(AccessTools.Constructor(typeof(ZRoutedRpc), new[] { typeof(bool) }))!
                .Postfixes.Any(p => p.owner == "org.serverbound.valheim.OwnerSkills"), "client RPC registration needs a local Serverbound");
            Check(Harmony.GetPatchInfo(AccessTools.Method(typeof(Player), "FixedUpdate"))!
                .Postfixes.Any(p => p.owner == "org.serverbound.valheim.OwnerSkills"), "client publisher needs a local Serverbound");
            Check(errors.Count == 0, string.Join("\n", errors));
        });
        Case("without a local simulation mod waits until the real server confirms Core", () =>
        {
            InitializeSkills(impact); Player player = Player.m_localPlayer;
            ZDO zdo = ((ZNetView)AccessTools.Field(typeof(Character), "m_nview").GetValue(player)).GetZDO();
            AccessTools.Method(skills, "Publish").Invoke(null, new object[] { player });
            Check(zdo.GetInt("Serverbound.skillProtocol") == 0, "published to a server without Core");
            AccessTools.Method(skills, "Status").Invoke(null, new object[] { 999L, true });
            Check(zdo.GetInt("Serverbound.skillProtocol") == 0, "untrusted status accepted");
            AccessTools.Method(skills, "Status").Invoke(null, new object[] { 100L, true });
            Check(zdo.GetInt("Serverbound.skillProtocol") == 1, "valid status did not publish");
            foreach (string key in new[] { "Serverbound.mining", "Serverbound.woodcutting", "Serverbound.animalHandling" })
                Check(zdo.GetFloat(key) == 0.75f, "wrong published factor " + key);
            AccessTools.Method(skills, "Status").Invoke(null, new object[] { 100L, false });
            Check(!(bool)AccessTools.Field(skills, "coreOnServer").GetValue(null), "disabled Core not respected");
        });
        Case("rejects forged or invalid XP and awards the intended local player once", () =>
        {
            InitializeSkills(impact);
            AccessTools.Method(skills, "Status").Invoke(null, new object[] { 100L, true });
            Skills.SkillType animal = (Skills.SkillType)AccessTools.Field(impact.GetType("ImpactfulSkills.patches.AnimalWhisper", true), "AnimalHandling").GetValue(null);
            void Send(long sender, ZDOID actor, Skills.SkillType skill, float amount)
            {
                var p = new ZPackage(); p.Write(actor); p.Write((int)skill); p.Write(amount); p.SetPos(0);
                AccessTools.Method(skills, "GrantXP").Invoke(null, new object[] { sender, p });
            }
            ZDOID id = Player.m_localPlayer.GetZDOID();
            Send(999, id, animal, 1); Send(100, new ZDOID(800, 1), animal, 1);
            Send(100, id, Skills.SkillType.Pickaxes, 1); Send(100, id, animal, -1);
            Send(100, id, animal, float.NaN); Send(100, id, animal, float.PositiveInfinity);
            Check(received.Count == 0, "invalid XP accepted");
            Send(100, id, animal, 1.25f);
            Check(received.Count == 1 && received[0].Skill == animal && received[0].Amount == 1.25f, "wrong XP award");
        });
        Case("duplicate RPC registration on one routed instance is idempotent", () =>
        {
            InitializeSkills(impact);
            AccessTools.Method(skills, "RegisterRpc").Invoke(null, new object[] { ZRoutedRpc.instance });
            AccessTools.Method(skills, "RegisterRpc").Invoke(null, new object[] { ZRoutedRpc.instance });
        });
        Case("suppresses owner bonuses on clients receiving server-owned mining broadcasts", () =>
        {
            InitializeSkills(impact);
            AccessTools.Method(skills, "Status").Invoke(null, new object[] { 100L, true });
            var rock = Fake<MineRock5>(); var view = Fake<ZNetView>();
            ZDO zdo = (ZDO)FormatterServices.GetUninitializedObject(typeof(ZDO)); zdo.m_uid = new ZDOID(400, 1); zdo.SetOwnerInternal(100);
            AccessTools.Field(typeof(ZNetView), "m_zdo").SetValue(view, zdo); AccessTools.Field(typeof(MineRock5), "m_nview").SetValue(rock, view);
            Check(!(bool)AccessTools.Method(skills, "OncePerMiningArea").Invoke(null, new object[] { rock, 0, 0f }), "client spawned server-owner bonus");
            zdo.SetOwnerInternal(900);
            Check((bool)AccessTools.Method(skills, "OncePerMiningArea").Invoke(null, new object[] { rock, 0, 0f }), "client-owned behavior changed");
        });
    }
    private static void InitializeSkills(Assembly impact)
    {
        AccessTools.Method(skills, "Prepare").Invoke(null, new object?[] { impact });
        var fixture = new Harmony("Serverbound.Tests.ClientSkills");
        fixture.Patch(AccessTools.Method(typeof(ZDOMan), "GetSessionID"), prefix: Hook(nameof(UID)));
        fixture.Patch(AccessTools.Method(typeof(ZNet), "GetUID"), prefix: Hook(nameof(UID)));
        fixture.Patch(AccessTools.Method(typeof(ZDO), "IncreaseDataRevision"), prefix: Hook(nameof(Skip)));
        fixture.Patch(AccessTools.Method(typeof(Player), "GetSkillFactor"), prefix: Hook(nameof(LocalFactor)));
        fixture.Patch(AccessTools.Method(typeof(Player), "RaiseSkill", new[] { typeof(Skills.SkillType), typeof(float) }), prefix: Hook(nameof(Award)));
        var rpc = new ZRoutedRpc(false); rpc.SetUID(900);
        var peer = (ZNetPeer)FormatterServices.GetUninitializedObject(typeof(ZNetPeer)); peer.m_uid = 100;
        ((List<ZNetPeer>)AccessTools.Field(typeof(ZRoutedRpc), "m_peers").GetValue(rpc)).Add(peer);
        var player = Fake<Player>(); var view = Fake<ZNetView>();
        var zdo = (ZDO)FormatterServices.GetUninitializedObject(typeof(ZDO)); zdo.m_uid = new ZDOID(900, (uint)++sequence); zdo.SetOwnerInternal(900);
        AccessTools.Field(typeof(ZNetView), "m_zdo").SetValue(view, zdo); AccessTools.Field(typeof(Character), "m_nview").SetValue(player, view);
        Player.m_localPlayer = player;
    }
}
