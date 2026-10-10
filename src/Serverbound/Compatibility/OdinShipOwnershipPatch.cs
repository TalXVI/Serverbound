using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace Serverbound.Compatibility
{
    internal static class OdinShipOwnershipPatch
    {
        internal const string Owner = Plugin.PluginGUID + ".OdinShip";
        private const string PluginGuid = "marlthon.OdinShip";
        private const string RequestRpc = "Serverbound.OdinShip.Change";
        private const string ResultRpc = "Serverbound.OdinShip.Result";
        private const float InteractionRange = 8f;
        private const float RequestTimeoutSeconds = 5f;

        private enum ChangeAction
        {
            Figurehead = 1,
            Sail = 2,
            Shields = 3,
            Hull = 4,
            TurretMode = 5
        }

        private enum InteractionKind
        {
            Controls,
            Turret
        }

        private sealed class PendingRequest
        {
            internal ZNetView View = null!;
            internal long ExpectedOwner;
            internal float ExpiresAt;
            internal string Label = "ship change";
        }

        private static readonly Dictionary<int, PendingRequest> pendingChanges = new Dictionary<int, PendingRequest>();
        private static readonly Dictionary<ZDOID, PendingRequest> pendingNames = new Dictionary<ZDOID, PendingRequest>();
        private static ConditionalWeakTable<ZNetView, object> registeredViews = new ConditionalWeakTable<ZNetView, object>();
        private static Assembly? vendorAssembly;
        private static Type? customizationType;
        private static FieldInfo? customizationViewField;
        private static FieldInfo? figureheadsField;
        private static FieldInfo? sailsField;
        private static FieldInfo? shieldsField;
        private static FieldInfo? hullsField;
        private static bool installed;
        private static bool pending;
        private static bool? customizationInputRewritten;
        private static bool? turretInputRewritten;
        private static bool absentLogged;
        private static int nextRequestId;

        internal static bool IsInstalled => installed;

        internal static void Prepare(Assembly? vendor)
        {
            if (vendor == null)
            {
                if (vendorAssembly == null && !absentLogged)
                {
                    CompatibilityInstaller.Info("OdinShip: optional mod absent; owner-routed input inactive.");
                    absentLogged = true;
                }
                return;
            }
            absentLogged = false;
            if ((installed || pending) && ReferenceEquals(vendorAssembly, vendor)) return;

            var harmony = new Harmony(Owner);
            harmony.UnpatchSelf();
            installed = false;
            pending = false;
            customizationInputRewritten = null;
            turretInputRewritten = null;
            vendorAssembly = null;
            customizationType = null;
            customizationViewField = null;
            figureheadsField = null;
            sailsField = null;
            shieldsField = null;
            hullsField = null;
            registeredViews = new ConditionalWeakTable<ZNetView, object>();
            pendingChanges.Clear();
            pendingNames.Clear();

            try
            {
                Guard.Build(vendor, ExpectedBuilds.OdinShip);

                Type customization = Guard.Type(vendor, "OdinShip.ShipCustomization");
                Type customizationPatch = Guard.Type(vendor, "OdinShip.ShipCustomizationPatch");
                Type turretModePatch = Guard.Type(vendor, "OdinShip.TurretModePatch");

                FieldInfo viewField = AccessTools.Field(customization, "m_nview")
                    ?? throw new MissingFieldException(customization.FullName, "m_nview");
                FieldInfo decorationField = AccessTools.Field(customization, "FigureheadObjects")
                    ?? throw new MissingFieldException(customization.FullName, "FigureheadObjects");
                FieldInfo sailField = AccessTools.Field(customization, "SailMaterials")
                    ?? throw new MissingFieldException(customization.FullName, "SailMaterials");
                FieldInfo shieldField = AccessTools.Field(customization, "ShieldMaterials")
                    ?? throw new MissingFieldException(customization.FullName, "ShieldMaterials");
                FieldInfo hullField = AccessTools.Field(customization, "HullMaterials")
                    ?? throw new MissingFieldException(customization.FullName, "HullMaterials");

                MethodInfo setup = Guard.Method(customization, "Setup", typeof(void));
                MethodInfo setText = Guard.Method(customization, "SetText", typeof(void), typeof(string));
                MethodInfo setShipName = Guard.Method(customization, "RPC_SetShipName", typeof(void), typeof(long), typeof(string));
                MethodInfo cycleObject = Guard.Method(customizationPatch, "CycleObject", typeof(void), typeof(ZNetView),
                    typeof(List<GameObject>), typeof(string), typeof(string));
                MethodInfo cycleMaterial = Guard.Method(customizationPatch, "CycleMaterial", typeof(void), typeof(ZNetView),
                    typeof(List<Material>), typeof(string), typeof(string));
                MethodInfo customizationInput = Guard.Method(customizationPatch, "Player_Update_Postfix", typeof(void), typeof(Player));
                MethodInfo turretInput = Guard.Method(turretModePatch, "Player_Update_Postfix", typeof(void), typeof(Player));

                customizationType = customization;
                customizationViewField = viewField;
                figureheadsField = decorationField;
                sailsField = sailField;
                shieldsField = shieldField;
                hullsField = hullField;

                harmony.Patch(setup, postfix: Guard.Hook(typeof(OdinShipOwnershipPatch), nameof(RegisterShipRpcs)));
                harmony.Patch(setText,
                    prefix: Guard.Hook(typeof(OdinShipOwnershipPatch), nameof(TrackNameRequest)),
                    finalizer: Guard.Hook(typeof(OdinShipOwnershipPatch), nameof(FinishNameSubmission)));
                harmony.Patch(setShipName,
                    prefix: Guard.Hook(typeof(OdinShipOwnershipPatch), nameof(ValidateNameRequest)),
                    postfix: Guard.Hook(typeof(OdinShipOwnershipPatch), nameof(CompleteNameRequest)));
                harmony.Patch(cycleObject, prefix: Guard.Hook(typeof(OdinShipOwnershipPatch), nameof(RouteObjectCustomization)));
                harmony.Patch(cycleMaterial, prefix: Guard.Hook(typeof(OdinShipOwnershipPatch), nameof(RouteMaterialCustomization)));
                harmony.Patch(customizationInput, transpiler: Guard.Hook(typeof(OdinShipOwnershipPatch), nameof(CustomizationInputGate)));
                harmony.Patch(turretInput, transpiler: Guard.Hook(typeof(OdinShipOwnershipPatch), nameof(TurretInputGate)));

                vendorAssembly = vendor;
                pending = true;
                CompatibilityInstaller.Info("OdinShip: 0.8.7 build verified; awaiting owner-routing verification.");
            }
            catch (Exception error)
            {
                harmony.UnpatchSelf();
                installed = false;
                pending = false;
                vendorAssembly = null;
                customizationType = null;
                customizationViewField = null;
                figureheadsField = null;
                sailsField = null;
                shieldsField = null;
                hullsField = null;
                pendingChanges.Clear();
                pendingNames.Clear();
                CompatibilityInstaller.Error("OdinShip: NOT APPLIED; owner-routed input rejected. " + error);
            }
        }

        internal static void Verify()
        {
            if ((!installed && !pending) || vendorAssembly == null) return;
            try
            {
                VerifyPatches();
                installed = true;
                if (pending)
                    CompatibilityInstaller.Info("OdinShip: APPLIED; non-owner customization and turret-mode requests route to the current object owner.");
                pending = false;
            }
            catch (Exception error)
            {
                new Harmony(Owner).UnpatchSelf();
                installed = false;
                pending = false;
                pendingChanges.Clear();
                pendingNames.Clear();
                CompatibilityInstaller.Error("OdinShip: NOT APPLIED; owner-routed input verification failed. " + error);
            }
        }

        private static void VerifyPatches()
        {
            if (vendorAssembly == null) throw new InvalidOperationException("OdinShip integration is not prepared.");
            if (customizationInputRewritten != true || turretInputRewritten != true)
                throw new NotSupportedException("OdinShip input owner checks did not match the audited IL.");
            Type customization = Guard.Type(vendorAssembly, "OdinShip.ShipCustomization");
            Type customizationPatch = Guard.Type(vendorAssembly, "OdinShip.ShipCustomizationPatch");
            Type turretModePatch = Guard.Type(vendorAssembly, "OdinShip.TurretModePatch");
            RequirePatch(Guard.Method(customization, "Setup", typeof(void)),
                nameof(RegisterShipRpcs), HarmonyPatchType.Postfix);
            RequirePatch(Guard.Method(customization, "SetText", typeof(void), typeof(string)),
                nameof(TrackNameRequest), HarmonyPatchType.Prefix);
            RequirePatch(Guard.Method(customization, "SetText", typeof(void), typeof(string)),
                nameof(FinishNameSubmission), HarmonyPatchType.Finalizer);
            MethodInfo rename = Guard.Method(customization, "RPC_SetShipName", typeof(void), typeof(long), typeof(string));
            RequirePatch(rename, nameof(ValidateNameRequest), HarmonyPatchType.Prefix);
            RequirePatch(rename, nameof(CompleteNameRequest), HarmonyPatchType.Postfix);
            RequirePatch(Guard.Method(customizationPatch, "CycleObject", typeof(void), typeof(ZNetView),
                typeof(List<GameObject>), typeof(string), typeof(string)), nameof(RouteObjectCustomization), HarmonyPatchType.Prefix);
            RequirePatch(Guard.Method(customizationPatch, "CycleMaterial", typeof(void), typeof(ZNetView),
                typeof(List<Material>), typeof(string), typeof(string)), nameof(RouteMaterialCustomization), HarmonyPatchType.Prefix);
            RequirePatch(Guard.Method(customizationPatch, "Player_Update_Postfix", typeof(void), typeof(Player)),
                nameof(CustomizationInputGate), HarmonyPatchType.Transpiler);
            RequirePatch(Guard.Method(turretModePatch, "Player_Update_Postfix", typeof(void), typeof(Player)),
                nameof(TurretInputGate), HarmonyPatchType.Transpiler);
        }

        internal static void Update()
        {
            if (!installed || (pendingChanges.Count == 0 && pendingNames.Count == 0)) return;
            if (ZNet.instance == null || ZRoutedRpc.instance == null)
            {
                pendingChanges.Clear();
                pendingNames.Clear();
                return;
            }

            float now = Time.unscaledTime;
            foreach (int id in pendingChanges.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray())
            {
                pendingChanges.Remove(id);
                ShowMessage("The ship owner did not respond. Ownership may have changed; try again.");
            }
            foreach (ZDOID id in pendingNames.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray())
            {
                pendingNames.Remove(id);
                ShowMessage("The ship owner did not respond. Ownership may have changed; try again.");
            }
        }

        internal static void ResetForTests()
        {
            new Harmony(Owner).UnpatchSelf();
            pendingChanges.Clear();
            pendingNames.Clear();
            registeredViews = new ConditionalWeakTable<ZNetView, object>();
            vendorAssembly = null;
            customizationType = null;
            customizationViewField = null;
            figureheadsField = null;
            sailsField = null;
            shieldsField = null;
            hullsField = null;
            installed = false;
            pending = false;
            customizationInputRewritten = null;
            turretInputRewritten = null;
            absentLogged = false;
            nextRequestId = 0;
        }

        internal static bool IsAuthorizedRequest(bool ownsCurrentObject, bool senderReady, bool playerAvailable,
            bool playerAlive, bool withinRange, bool shipAvailable, bool helmOccupied, bool requesterAtHelm,
            bool cargoInUse, bool requesterOwnsCargo, out string rejection)
        {
            if (!ownsCurrentObject) rejection = "Ship ownership changed. Try again.";
            else if (!senderReady) rejection = "Your connection is not ready to change this ship.";
            else if (!playerAvailable) rejection = "Your player is no longer available near this ship.";
            else if (!playerAlive) rejection = "You must be alive to change this ship.";
            else if (!withinRange) rejection = "Move closer to the ship and try again.";
            else if (!shipAvailable) rejection = "The ship is unavailable.";
            else if (helmOccupied && !requesterAtHelm) rejection = "Only the current helm user can change this ship.";
            else if (cargoInUse && !requesterOwnsCargo) rejection = "Ship cargo is in use by another player.";
            else
            {
                rejection = string.Empty;
                return true;
            }
            return false;
        }

        internal static bool TryNextIndex(int current, int count, out int next)
        {
            next = 0;
            if (count <= 0) return false;
            int normalized = current % count;
            if (normalized < 0) normalized += count;
            next = (normalized + 1) % count;
            return true;
        }

        private static IEnumerable<CodeInstruction> CustomizationInputGate(IEnumerable<CodeInstruction> instructions)
        {
            return ReplaceOwnerCheck(instructions, nameof(CanUseCustomizationInputs), out customizationInputRewritten);
        }

        private static IEnumerable<CodeInstruction> TurretInputGate(IEnumerable<CodeInstruction> instructions)
        {
            return ReplaceOwnerCheck(instructions, nameof(TryRouteTurretMode), out turretInputRewritten);
        }

        private static IEnumerable<CodeInstruction> ReplaceOwnerCheck(IEnumerable<CodeInstruction> instructions, string helperName,
            out bool? rewritten)
        {
            var codes = instructions.Select(code => new CodeInstruction(code)).ToList();
            MethodInfo ownerCheck = AccessTools.Method(typeof(ZNetView), nameof(ZNetView.IsOwner), Type.EmptyTypes);
            rewritten = codes.Count(code => code.Calls(ownerCheck)) == 1;
            if (rewritten != true) return codes;

            MethodInfo helper = AccessTools.Method(typeof(OdinShipOwnershipPatch), helperName, new[] { typeof(ZNetView) });
            foreach (CodeInstruction code in codes)
            {
                if (code.Calls(ownerCheck))
                {
                    code.opcode = OpCodes.Call;
                    code.operand = helper;
                }
            }
            return codes;
        }

        private static bool CanUseCustomizationInputs(ZNetView view)
        {
            if (view == null || !view.IsValid()) return false;
            if (!installed) return view.IsOwner();
            return view.IsOwner() || IsLocalPlayerNear(view, InteractionKind.Controls);
        }

        // OdinShip checks its configured key before this gate. A non-owner routes the already-fired action and
        // returns false so the vendor's direct ZDO write and success message do not run on that client.
        private static bool TryRouteTurretMode(ZNetView view)
        {
            if (view == null || !view.IsValid()) return false;
            if (!installed) return view.IsOwner();
            if (view.IsOwner()) return true;
            GameObject? hover = Player.m_localPlayer?.GetHoverObject();
            Turret? turret = hover != null ? hover.GetComponentInParent<Turret>() : null;
            Ship? ship = view.GetComponent<Ship>();
            if (ship == null || !IsCustomTurret(turret) || !BelongsToShip(view, ship, turret!))
            {
                ShowMessage("This turret is unavailable.");
                return false;
            }
            StartRequest(view, ChangeAction.TurretMode, "ballista mode");
            return false;
        }

        private static bool RouteObjectCustomization(ZNetView __0, string __2)
        {
            return RouteCustomization(__0, __2);
        }

        private static bool RouteMaterialCustomization(ZNetView __0, string __2)
        {
            return RouteCustomization(__0, __2);
        }

        private static bool RouteCustomization(ZNetView view, string key)
        {
            if (!installed) return true;
            if (view == null || !view.IsValid()) return false;
            if (view.IsOwner()) return true;
            if (!TryCustomizationAction(key, out ChangeAction action))
            {
                ShowMessage("This ship customization is unavailable.");
                return false;
            }
            StartRequest(view, action, "ship customization");
            return false;
        }

        private static bool TryCustomizationAction(string key, out ChangeAction action)
        {
            switch (key)
            {
                case "odinship_figurehead_index": action = ChangeAction.Figurehead; return true;
                case "odinship_sail_index": action = ChangeAction.Sail; return true;
                case "odinship_shield_index": action = ChangeAction.Shields; return true;
                case "odinship_hull_index": action = ChangeAction.Hull; return true;
                default: action = default; return false;
            }
        }

        private static string KeyFor(ChangeAction action)
        {
            switch (action)
            {
                case ChangeAction.Figurehead: return "odinship_figurehead_index";
                case ChangeAction.Sail: return "odinship_sail_index";
                case ChangeAction.Shields: return "odinship_shield_index";
                case ChangeAction.Hull: return "odinship_hull_index";
                default: throw new ArgumentOutOfRangeException(nameof(action));
            }
        }

        private static FieldInfo? ListFieldFor(ChangeAction action)
        {
            switch (action)
            {
                case ChangeAction.Figurehead: return figureheadsField;
                case ChangeAction.Sail: return sailsField;
                case ChangeAction.Shields: return shieldsField;
                case ChangeAction.Hull: return hullsField;
                default: return null;
            }
        }

        private static void RegisterShipRpcs(object __instance)
        {
            if (!installed || customizationViewField == null) return;
            try
            {
                ZNetView? view = customizationViewField.GetValue(__instance) as ZNetView;
                EnsureRpcRegistration(view);
            }
            catch (Exception error)
            {
                CompatibilityInstaller.Warning("OdinShip: could not register owner-routed ship requests. " + error.Message);
            }
        }

        private static void EnsureRpcRegistration(ZNetView? view)
        {
            if (view == null || !view.IsValid() || view.GetZDO() == null || registeredViews.TryGetValue(view, out _)) return;
            view.Register<int, int>(RequestRpc, (sender, requestId, action) => HandleChangeRequest(view, sender, requestId, action));
            view.Register<int, bool, string>(ResultRpc, (sender, requestId, accepted, message) => HandleResult(view, sender, requestId, accepted, message));
            registeredViews.Add(view, new object());
        }

        private static void StartRequest(ZNetView view, ChangeAction action, string label)
        {
            if (!IsLocalPlayerNear(view, action == ChangeAction.TurretMode ? InteractionKind.Turret : InteractionKind.Controls))
            {
                ShowMessage("Move closer to the ship and try again.");
                return;
            }
            ZDO? zdo = view.GetZDO();
            long owner = zdo != null ? zdo.GetOwner() : 0L;
            if (owner == 0L || ZRoutedRpc.instance == null)
            {
                ShowMessage("The ship has no available owner. Try again shortly.");
                return;
            }

            EnsureRpcRegistration(view);
            int requestId = NextRequestId();
            pendingChanges[requestId] = new PendingRequest
            {
                View = view,
                ExpectedOwner = owner,
                ExpiresAt = Time.unscaledTime + RequestTimeoutSeconds,
                Label = label
            };
            ShowMessage("Requesting " + label + " from the ship owner...");
            try
            {
                view.InvokeRPC(RequestRpc, requestId, (int)action);
            }
            catch (Exception error)
            {
                pendingChanges.Remove(requestId);
                CompatibilityInstaller.Warning("OdinShip: request could not be sent. " + error.Message);
                ShowMessage("The ship request could not be sent. Try again.");
            }
        }

        private static int NextRequestId()
        {
            do
            {
                nextRequestId = nextRequestId == int.MaxValue ? 1 : nextRequestId + 1;
            }
            while (nextRequestId == 0 || pendingChanges.ContainsKey(nextRequestId));
            return nextRequestId;
        }

        private static void HandleChangeRequest(ZNetView view, long sender, int requestId, int actionValue)
        {
            if (requestId <= 0) return;
            if (!Enum.IsDefined(typeof(ChangeAction), actionValue))
            {
                Reply(view, sender, requestId, false, "Unsupported ship change.");
                return;
            }

            ChangeAction action = (ChangeAction)actionValue;
            InteractionKind interaction = action == ChangeAction.TurretMode ? InteractionKind.Turret : InteractionKind.Controls;
            if (!TryAuthorize(view, sender, interaction, out _, out string rejection))
            {
                Reply(view, sender, requestId, false, rejection);
                return;
            }
            ZDO zdo = view.GetZDO();
            if (action == ChangeAction.TurretMode)
            {
                int next = (zdo.GetInt("odinship_turret_mode") + 1) % 3;
                zdo.Set("odinship_turret_mode", next);
                Reply(view, sender, requestId, true, "Ballista mode changed to " + ModeName(next) + ".");
                return;
            }

            FieldInfo? listField = ListFieldFor(action);
            object? component = customizationType != null ? view.GetComponent(customizationType) : null;
            ICollection? options = component != null && listField != null ? listField.GetValue(component) as ICollection : null;
            if (options == null || !TryNextIndex(zdo.GetInt(KeyFor(action)), options.Count, out int nextIndex))
            {
                Reply(view, sender, requestId, false, "This ship has no available option for that customization.");
                return;
            }

            zdo.Set(KeyFor(action), nextIndex);
            view.InvokeRPC(ZNetView.Everybody, "OnCustomizationChanged");
            Reply(view, sender, requestId, true, "Ship customization updated.");
        }

        private static bool TryAuthorize(ZNetView view, long sender, InteractionKind interaction, out Player? requester, out string rejection)
        {
            requester = null;
            if (view == null || !view.IsValid() || view.GetZDO() == null)
            {
                rejection = "Ship ownership changed. Try again.";
                return false;
            }
            ZDO? zdo = view.GetZDO();
            long owner = zdo != null ? zdo.GetOwner() : 0L;
            bool ownsHere = zdo != null && view.IsOwner() && owner == LocalPeerId();
            bool senderReady = IsReadyPeer(sender);
            requester = senderReady ? PlayerForPeer(sender) : null;
            long playerId = requester != null ? requester.GetPlayerID() : 0L;
            bool playerAvailable = requester != null && requester.GetOwner() == sender && playerId != 0;
            bool playerAlive = playerAvailable && !requester!.IsDead();
            bool withinRange = playerAvailable && IsPlayerNear(view, interaction, requester!);

            Ship? ship = view.GetComponent<Ship>();
            bool shipAvailable = ship != null;
            ShipControlls? controls = ship != null ? ship.m_shipControlls : null;
            bool helmOccupied = controls != null && controls.HaveValidUser();
            long helmUser = helmOccupied ? controls!.GetUser() : 0L;
            bool requesterAtHelm = helmOccupied && playerAvailable && helmUser == playerId;
            bool cargoInUse = zdo != null && zdo.GetInt("InUse", 0) != 0;
            bool requesterOwnsCargo = zdo != null && owner == sender;

            if (!IsAuthorizedRequest(ownsHere, senderReady, playerAvailable, playerAlive, withinRange, shipAvailable,
                helmOccupied, requesterAtHelm, cargoInUse, requesterOwnsCargo, out rejection)) return false;
            if (!HasWardAccess(view.transform.position, playerId))
            {
                rejection = "A ward blocks access to this ship.";
                return false;
            }
            return true;
        }

        private static bool HasWardAccess(Vector3 position, long playerId)
        {
            // Check the sender's profile ID. PrivateArea.CheckAccess uses the local
            // player, which can be a different owner or absent on a dedicated server.
            foreach (PrivateArea ward in PrivateArea.m_allAreas)
                if (ward != null && ward.IsEnabled() && ward.IsInside(position, 0f)
                    && ward.m_piece.GetCreator() != playerId && !ward.IsPermitted(playerId)) return false;
            return true;
        }

        private static bool IsLocalPlayerNear(ZNetView view, InteractionKind interaction)
        {
            Player? player = Player.m_localPlayer;
            if (view == null || !view.IsValid() || player == null || player.IsDead()) return false;
            return IsPlayerNear(view, interaction, player);
        }

        private static bool IsPlayerNear(ZNetView view, InteractionKind interaction, Player player)
        {
            Vector3 position = player.transform.position;
            return TryGetInteractionAnchor(view, interaction, position, out Vector3 anchor)
                && (position - anchor).sqrMagnitude <= InteractionRange * InteractionRange;
        }

        private static bool TryGetInteractionAnchor(ZNetView view, InteractionKind interaction, Vector3 position, out Vector3 anchor)
        {
            anchor = default;
            Ship? ship = view.GetComponent<Ship>();
            if (ship == null) return false;
            if (interaction == InteractionKind.Controls)
            {
                ShipControlls? controls = ship.m_shipControlls;
                object? customization = customizationType != null ? view.GetComponent(customizationType) : null;
                if (controls == null || !ReferenceEquals(controls.m_ship, ship) || !BelongsToShip(view, ship, controls)
                    || customization == null || !ReferenceEquals(customizationViewField?.GetValue(customization), view)) return false;
                anchor = controls.GetPosition();
                return true;
            }

            float closest = float.PositiveInfinity;
            foreach (Turret turret in view.GetComponentsInChildren<Turret>(true))
            {
                if (!IsCustomTurret(turret) || !BelongsToShip(view, ship, turret)) continue;
                Vector3 candidate = turret.transform.position;
                float distance = (candidate - position).sqrMagnitude;
                if (distance >= closest) continue;
                closest = distance;
                anchor = candidate;
            }
            return !float.IsPositiveInfinity(closest);
        }

        private static bool BelongsToShip(ZNetView view, Ship ship, Component component)
        {
            return ReferenceEquals(component.GetComponentInParent<ZNetView>(), view)
                && ReferenceEquals(component.GetComponentInParent<Ship>(), ship);
        }

        private static bool IsCustomTurret(Turret? turret)
        {
            if (turret == null || string.IsNullOrEmpty(turret.name)) return false;
            string name = turret.name.Replace("(Clone)", "").Trim();
            return name.Equals("warshipturret", StringComparison.OrdinalIgnoreCase)
                || name.Equals("skuldelevturret", StringComparison.OrdinalIgnoreCase)
                || name.Equals("caravelturret", StringComparison.OrdinalIgnoreCase)
                || name.Equals("tauruswarshipturret", StringComparison.OrdinalIgnoreCase);
        }

        private static string ModeName(int mode)
        {
            switch (mode)
            {
                case 1: return "Creatures only";
                case 2: return "Creatures and players";
                default: return "Off";
            }
        }

        private static bool TrackNameRequest(object __instance, string text, out PendingRequest? __state)
        {
            __state = null;
            if (!installed || customizationViewField == null) return true;
            ZNetView? view = customizationViewField.GetValue(__instance) as ZNetView;
            if (view == null || !view.IsValid() || view.GetZDO() == null)
            {
                ShowMessage("This ship is unavailable.");
                return false;
            }
            if (!IsLocalPlayerNear(view, InteractionKind.Controls))
            {
                ShowMessage("Move closer to the ship and try again.");
                return false;
            }
            if (view.GetZDO().GetOwner() == 0L || ZRoutedRpc.instance == null)
            {
                ShowMessage("The ship has no available owner. Try again shortly.");
                return false;
            }
            try { EnsureRpcRegistration(view); }
            catch (Exception error)
            {
                CompatibilityInstaller.Warning("OdinShip: rename results could not be registered. " + error.Message);
                ShowMessage("The ship name request could not be sent. Try again.");
                return false;
            }
            __state = new PendingRequest
            {
                View = view,
                ExpectedOwner = view.GetZDO().GetOwner(),
                ExpiresAt = Time.unscaledTime + RequestTimeoutSeconds,
                Label = "ship name"
            };
            pendingNames[view.GetZDO().m_uid] = __state;
            ShowMessage("Sending the ship name to its owner...");
            return true;
        }

        private static void FinishNameSubmission(Exception? __exception, PendingRequest? __state)
        {
            if (__exception == null || __state == null) return;
            var pendingName = pendingNames.FirstOrDefault(pair => ReferenceEquals(pair.Value, __state));
            if (pendingName.Value == null) return;
            pendingNames.Remove(pendingName.Key);
            CompatibilityInstaller.Warning("OdinShip: ship name request failed. " + __exception.Message);
            ShowMessage("The ship name request could not complete. Try again.");
        }

        private static bool ValidateNameRequest(object __instance, long sender, string name, out bool __state)
        {
            __state = false;
            if (!installed || customizationViewField == null) return true;
            ZNetView? view = customizationViewField.GetValue(__instance) as ZNetView;
            string failure = name == null ? "The ship name is invalid." : "Ship ownership or access changed. Try again.";
            string rejection = string.Empty;
            if (view != null && TryAuthorize(view, sender, InteractionKind.Controls, out _, out rejection))
            {
                if (name == null)
                {
                    Reply(view, sender, 0, false, failure);
                    return false;
                }
                __state = true;
                return true;
            }
            if (!string.IsNullOrEmpty(rejection)) failure = rejection;
            Reply(view, sender, 0, false, failure);
            return false;
        }

        private static void CompleteNameRequest(object __instance, long sender, bool __state)
        {
            if (!__state || customizationViewField == null) return;
            ZNetView? view = customizationViewField.GetValue(__instance) as ZNetView;
            Reply(view, sender, 0, true, "Ship name updated.");
        }

        private static void HandleResult(ZNetView view, long sender, int requestId, bool accepted, string message)
        {
            if (view == null || !view.IsValid() || view.GetZDO() == null) return;
            PendingRequest? pending;
            if (requestId == 0)
            {
                ZDOID id = view.GetZDO().m_uid;
                if (!pendingNames.TryGetValue(id, out pending) || !ReferenceEquals(pending.View, view)) return;
                if (!IsExpectedOwner(view, sender, pending)) return;
                pendingNames.Remove(id);
            }
            else
            {
                if (!pendingChanges.TryGetValue(requestId, out pending) || !ReferenceEquals(pending.View, view)) return;
                if (!IsExpectedOwner(view, sender, pending)) return;
                pendingChanges.Remove(requestId);
            }
            ShowMessage(message ?? (accepted ? "Ship change accepted." : "Ship change rejected."));
        }

        private static bool IsExpectedOwner(ZNetView view, long sender, PendingRequest pending)
        {
            return sender == pending.ExpectedOwner || (view.GetZDO() != null && view.GetZDO().GetOwner() == sender);
        }

        private static void Reply(ZNetView? view, long target, int requestId, bool accepted, string message)
        {
            if (view == null || !view.IsValid() || target == 0 || !IsReadyPeer(target)) return;
            try { view.InvokeRPC(target, ResultRpc, requestId, accepted, message); }
            catch (Exception error) { CompatibilityInstaller.Warning("OdinShip: result could not be sent to the requester. " + error.Message); }
        }

        private static bool IsReadyPeer(long peerId)
        {
            if (peerId == 0 || ZNet.instance == null) return false;
            if (peerId == LocalPeerId()) return PlayerForPeer(peerId) != null;
            ZRoutedRpc? router = ZRoutedRpc.instance;
            if (router == null) return false;
            if (router.m_server) return router.m_peers.Any(peer => peer.m_uid == peerId && peer.IsReady());
            // A client receives other clients' routed requests through its server connection.
            return router.m_peers.Any(peer => peer.m_server && peer.IsReady()) && PlayerForPeer(peerId) != null;
        }

        private static Player? PlayerForPeer(long peerId) => peerId == 0 ? null
            : Player.GetAllPlayers().FirstOrDefault(player => player != null && player.GetOwner() == peerId);

        private static long LocalPeerId() => ZNet.instance != null ? ZNet.GetUID() : 0L;

        private static void ShowMessage(string text)
        {
            Player? player = Player.m_localPlayer;
            if (player != null) player.Message(MessageHud.MessageType.TopLeft, text);
        }

        private static void RequirePatch(MethodBase target, string hookName, HarmonyPatchType type)
        {
            Patches? patches = Harmony.GetPatchInfo(target);
            IEnumerable<Patch>? registrations = type == HarmonyPatchType.Transpiler ? patches?.Transpilers
                : type == HarmonyPatchType.Postfix ? patches?.Postfixes
                : type == HarmonyPatchType.Finalizer ? patches?.Finalizers : patches?.Prefixes;
            bool found = registrations?.Any(patch => patch.owner == Owner && patch.PatchMethod.Name == hookName) == true;
            if (!found) throw new InvalidOperationException("OdinShip hook was not installed: " + target.DeclaringType?.FullName + "." + target.Name);
        }
    }
}
