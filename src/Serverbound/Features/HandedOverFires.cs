#nullable disable
using Serverbound.FeatureModel;
using HarmonyLib;
using Serverbound.Settings;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Serverbound.Features
{
	/*
		A spreading fire goes out when the piece, tree or log it burns on is gone:
		CinderSpawner.FixedUpdate destroys it on its owner once that object is. The object is kept
		in a plain field that CinderSpawner.Setup fills on the machine that lit the fire, from the
		collider the spark landed on or the fireplace reached (Valheim 1.0.17). Every other copy of
		the fire has none.

		Both fire prefabs are persistent, so when the player who lit one leaves its area or
		disconnects, this mod hands it to the server, whose copy never knew what it burns on. When
		the piece under it was destroyed, the fire kept burning in mid-air until its 30 s were up,
		still damaging what it touched.

		Fix: the first time the server runs such a fire, find what it burns on the way a spark
		would have: the nearest burnable piece, tree or log its flame touches, to within 5 cm.
	*/
	public class HandedOverFires : IFeature
	{
		private const float TouchRadius = 0.5f;
		private const int TouchSteps = 10;

		public bool FeatureEnabled()
		{
			return Configuration.fixHandedOverFires.Value;
		}

		private static readonly ConditionalWeakTable<CinderSpawner, object> s_searched = new ConditionalWeakTable<CinderSpawner, object>();
		private static readonly Collider[] s_touching = new Collider[32];
		private static int s_burnableMask;

		[HarmonyPatch(typeof(Fire), "UpdateFire")]
		public static class Fire_UpdateFire_Patch
		{
			static void Postfix(Fire __instance)
			{
				if (!__instance.m_nview.IsOwner())
				{
					return;
				}
				CinderSpawner spawner = __instance.GetComponent<CinderSpawner>();
				if (spawner == null || spawner.m_hasAttachObj || s_searched.TryGetValue(spawner, out _))
				{
					return;
				}
				s_searched.Add(spawner, null);
				Collider burning = BurningOn(__instance.transform);
				if (burning != null)
				{
					spawner.m_attachObj = burning.gameObject;
					spawner.m_hasAttachObj = true;
				}
			}
		}

		/*
			Widens a sphere around the flame in steps and takes the first burnable collider it
			touches. Only an overlap test follows each collider's own shape: a rotated collider's
			bounding box can contain the flame, and Collider.ClosestPoint returns the query point
			itself for the non-convex mesh colliders many trees and pieces use.
			The layers are those a spark can land on, without terrain and characters: Cinder.Awake's mask.
		*/
		private static Collider BurningOn(Transform fire)
		{
			if (s_burnableMask == 0)
			{
				s_burnableMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "piece_nonsolid", "vehicle");
			}
			Vector3 position = fire.position;
			for (int step = 1; step <= TouchSteps; step++)
			{
				int count = Physics.OverlapSphereNonAlloc(position, TouchRadius * step / TouchSteps, s_touching, s_burnableMask, QueryTriggerInteraction.Ignore);
				for (int i = 0; i < count; i++)
				{
					Collider collider = s_touching[i];
					if (!collider.transform.IsChildOf(fire) && Burnable(collider))
					{
						return collider;
					}
				}
			}
			return null;
		}

		// What Cinder.CanBurn lights apart from grass; wetness is left out, since the fire already burns.
		private static bool Burnable(Collider collider)
		{
			WearNTear piece = collider.GetComponentInParent<WearNTear>();
			if (piece != null)
			{
				return piece.m_burnable;
			}
			return collider.GetComponentInParent<TreeBase>() != null || collider.GetComponentInParent<TreeLog>() != null;
		}
	}
}
