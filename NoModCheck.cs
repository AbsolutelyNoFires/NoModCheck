using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Patch = AuroraPatch.Patch;

namespace NoModCheck
{
    // Neutralises the anti-modding checks added in Aurora 3.3:
    //  1. A detection method (l1.e() in 3.3.0) that asks Harmony whether Aurora is patched. If it is,
    //     the game sets a session flag that disables standing orders, auto commander assignment
    //     and industry "Create". Fix: force it to return false.
    //  2/3. Assembly.GetEntryAssembly() != Aurora -> Naval Organization / Economics skip their Load setup.
    //  4. Process.MainModule.FileName must end with "Aurora.exe" -> shipyard "Create Task".
    //  5. new StackTrace().GetFrames() containing an AuroraPatch frame -> Commanders race selector.
    // Fix for 2-5: inside Aurora's own methods only, redirect those calls to helpers that report
    // what a vanilla Aurora.exe launch would see. Everything is located by IL content, not by
    // obfuscated names, so it should survive renames in later builds.
    public class NoModCheck : Patch
    {
        public override string Description => "Neutralises Aurora 3.3's anti-modding checks.";

        private const string DetectionMarker = "GetAllPatchedMethods";

        private static Assembly s_aurora;
        private static string s_auroraPath;
        private static Dictionary<MethodBase, MethodInfo> s_redirects;

        protected override void Loaded(Harmony harmony)
        {
            s_aurora = AuroraAssembly;
            s_auroraPath = AuroraExecutablePath;
            s_redirects = new Dictionary<MethodBase, MethodInfo>
            {
                { AccessTools.Method(typeof(Assembly), nameof(Assembly.GetEntryAssembly)), AccessTools.Method(typeof(NoModCheck), nameof(EntryAssembly)) },
                { AccessTools.PropertyGetter(typeof(ProcessModule), nameof(ProcessModule.FileName)), AccessTools.Method(typeof(NoModCheck), nameof(ModuleFileName)) },
                { AccessTools.Method(typeof(StackTrace), nameof(StackTrace.GetFrames)), AccessTools.Method(typeof(NoModCheck), nameof(FramesWithoutPatcher)) },
            };

            var detection = new List<MethodInfo>();
            var redirected = new List<MethodBase>();
            ScanAurora(detection, redirected);

            var prefix = new HarmonyMethod(typeof(NoModCheck), nameof(ReturnFalse));
            foreach (var m in detection)
                TryPatch(() => harmony.Patch(m, prefix: prefix), m, "mod detection -> false");

            var transpiler = new HarmonyMethod(typeof(NoModCheck), nameof(Redirect));
            foreach (var m in redirected)
                TryPatch(() => harmony.Patch(m, transpiler: transpiler), m, "environment checks redirected");

            if (detection.Count == 0 && redirected.Count == 0)
                LogInfo("No anti-modding checks found (expected on pre-3.3 builds); nothing patched.");
        }

        private void TryPatch(Action patch, MethodBase m, string what)
        {
            try
            {
                patch();
                LogInfo($"{m.DeclaringType?.Name}.{m.Name}: {what}");
            }
            catch (Exception ex)
            {
                LogError($"Failed to patch {m.DeclaringType?.Name}.{m.Name}: {ex}");
            }
        }

        // ---- replacements -------------------------------------------------------------------

        private static bool ReturnFalse(ref bool __result)
        {
            __result = false;
            return false;
        }

        public static Assembly EntryAssembly() => s_aurora;

        public static string ModuleFileName(ProcessModule module) => s_auroraPath;

        public static StackFrame[] FramesWithoutPatcher(StackTrace trace)
        {
            var frames = trace.GetFrames();
            if (frames == null) return null;
            return frames.Where(f =>
            {
                try { return f.GetMethod()?.DeclaringType?.Assembly.GetName().Name != "AuroraPatch"; }
                catch { return true; }
            }).ToArray();
        }

        private static IEnumerable<CodeInstruction> Redirect(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var ins in instructions)
            {
                if ((ins.opcode == OpCodes.Call || ins.opcode == OpCodes.Callvirt)
                    && ins.operand is MethodBase target && s_redirects.TryGetValue(target, out var helper))
                {
                    ins.opcode = OpCodes.Call; // helpers are static; instance arg stays on the stack
                    ins.operand = helper;
                }
                yield return ins;
            }
        }

        // ---- discovery ----------------------------------------------------------------------

        private void ScanAurora(List<MethodInfo> detection, List<MethodBase> redirected)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            var module = s_aurora.ManifestModule;
            var tokenCache = new Dictionary<int, bool>();

            Type[] types;
            try { types = s_aurora.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }

            foreach (var type in types)
            {
                foreach (var m in type.GetMethods(flags).Cast<MethodBase>().Concat(type.GetConstructors(flags)))
                {
                    byte[] il;
                    try { il = m.GetMethodBody()?.GetILAsByteArray(); }
                    catch { continue; }
                    if (il == null) continue;

                    bool isDetection = false, needsRedirect = false;
                    for (int i = 0; i + 4 < il.Length; i++)
                    {
                        byte op = il[i];
                        if (op != 0x72 && op != 0x28 && op != 0x6F) continue; // ldstr, call, callvirt
                        int token = BitConverter.ToInt32(il, i + 1);
                        int table = (token >> 24) & 0xFF;
                        if (op == 0x72 && table == 0x70)
                        {
                            try { if (module.ResolveString(token) == DetectionMarker) isDetection = true; } catch { }
                        }
                        else if (op != 0x72 && (table == 0x0A || table == 0x06)) // MemberRef / MethodDef
                        {
                            if (!tokenCache.TryGetValue(token, out bool hit))
                            {
                                try { hit = s_redirects.ContainsKey(module.ResolveMethod(token)); } catch { hit = false; }
                                tokenCache[token] = hit;
                            }
                            if (hit) needsRedirect = true;
                        }
                    }

                    if (isDetection && m is MethodInfo mi && mi.ReturnType == typeof(bool) && mi.IsStatic && mi.GetParameters().Length == 0)
                        detection.Add(mi);
                    else if (needsRedirect)
                        redirected.Add(m);
                }
            }
        }
    }
}
