using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;
namespace Serverbound.Compatibility
{
    public static class Guard
    {
        internal static bool KnownServerBuild()
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream file = File.OpenRead(typeof(ZNet).Assembly.Location))
                return ExpectedBuilds.Server.Contains(BitConverter.ToString(sha.ComputeHash(file)).Replace("-", ""));
        }
        public static void Build(Assembly assembly, params string[] expectedHashes)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream file = File.OpenRead(assembly.Location))
            {
                string actual = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "");
                if (!expectedHashes.Contains(actual))
                    throw new NotSupportedException($"{assembly.GetName().Name} build changed; expected "
                        + $"{string.Join(" or ", expectedHashes)}, found {actual}. Re-audit required.");
            }
        }

        internal static Type Type(Assembly assembly, string name) =>
            assembly.GetType(name, throwOnError: true)!;

        internal static MethodInfo Method(Type type, string name, Type returnType, params Type[] parameters)
        {
            MethodInfo? method = type.GetMethod(name,
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, parameters, null);

            if (method == null || method.ReturnType != returnType)
            {
                throw new MissingMethodException(type.FullName, name);
            }

            return method;
        }

        internal static HarmonyMethod Hook(Type type, string name, int priority = Priority.Normal)
        {
            return new HarmonyMethod(AccessTools.DeclaredMethod(type, name)) { priority = priority };
        }

        internal static void Registered(MethodBase target)
        {
            CompatibilityInstaller.Info($"Registered: {Name(target)}");
        }

        internal static string Name(MethodBase target)
        {
            return $"{target.DeclaringType!.Assembly.GetName().Name}:{target.DeclaringType.FullName}.{target.Name}";
        }
    }
}
