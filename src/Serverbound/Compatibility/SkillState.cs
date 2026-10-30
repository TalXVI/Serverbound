using System;
using System.Collections.Generic;

namespace Serverbound.Compatibility
{
    // Pure rules shared by the publisher, owner-side readers and offline tests.
    public static class SkillState
    {
        public const int Protocol = 1;
        public static bool ValidFactor(float value) => !float.IsNaN(value) && !float.IsInfinity(value) && value >= 0 && value <= 1;
        // Status effects can raise a level above 100.
        public static bool ValidLevel(float value) => !float.IsNaN(value) && !float.IsInfinity(value) && value >= 0;

        public static T? Closest<T>(IEnumerable<T> players, Func<T, float> squaredDistance,
            Func<T, long> id, float range) where T : class
        {
            T? closest = null;
            float closestDistance = 0;
            long closestId = 0;
            float rangeSquared = range * range;

            foreach (T player in players)
            {
                float distance = squaredDistance(player);
                if (!(distance <= rangeSquared) || (closest != null && distance > closestDistance)) continue;

                long playerId = id(player);
                if (closest == null || distance < closestDistance
                    || (distance == closestDistance && playerId < closestId))
                {
                    closest = player;
                    closestDistance = distance;
                    closestId = playerId;
                }
            }

            return closest;
        }

        public static float RequireFactor(int protocol, float value)
        {
            if (protocol != Protocol || !ValidFactor(value))
                throw new InvalidOperationException("Missing or invalid Serverbound skill state. All players must use the matching compatibility package.");
            return value;
        }

        public static float RequireLevel(int protocol, float value)
        {
            if (protocol != Protocol || !ValidLevel(value))
                throw new InvalidOperationException("Missing or invalid Serverbound skill level. All players must use the matching compatibility package.");
            return value;
        }
    }
}
