using System.Reflection;

namespace HarmonyLib
{
    public class Harmony
    {
        public Harmony(string id)
        {
        }

        public void Patch(MethodBase original, HarmonyMethod prefix = null, HarmonyMethod postfix = null)
        {
        }
    }

    public class HarmonyMethod
    {
        public HarmonyMethod(MethodInfo method)
        {
        }

        public HarmonyMethod(System.Type type, string methodName)
        {
        }

        public int priority { get; set; }
    }

    public static class Priority
    {
        public const int First = -2147483648;
        public const int Last = 2147483647;
    }
}
