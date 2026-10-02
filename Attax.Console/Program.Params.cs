using Attax.Core;
using Attax.Play;

namespace Attax.Console {
    partial class Program {
        static void RunParamsDefault(ParamsDefaultOptions o) {
            if (File.Exists(o.Out) && !o.Force) throw new IOException($"'{o.Out}' already exists; pass --force to overwrite it.");
            var p = EngineParamsFile.LoadBase();
            EngineParamsFile.Save(p, o.Out);
            System.Console.WriteLine($"[params] wrote a complete copy of {EngineParamsFile.BasePath()} to {o.Out} (fingerprint {EngineParamsFile.Fingerprint(p)}).");
            System.Console.WriteLine("[params] a params= file only needs the values it changes: delete the rest (a table that is present must be complete). Check it with: params-check --file " + o.Out);
        }

        static void RunParamsCheck(ParamsCheckOptions o) {
            var baseP = EngineParamsFile.LoadBase();
            var p = EngineParamsFile.Load(o.File);   // base + this file; throws with file/property/line on any problem
            var diff = EngineParamsFile.Diff(baseP, p);
            System.Console.WriteLine($"[params] {o.File}: valid on top of {EngineParamsFile.BasePath()}. fingerprint {EngineParamsFile.Fingerprint(p)} (base: {EngineParamsFile.Fingerprint(baseP)}).");
            if (diff.Count == 0) System.Console.WriteLine("[params] identical to the base file.");
            else {
                System.Console.WriteLine($"[params] {diff.Count} value(s) differ from the base file:");
                foreach (var d in diff) System.Console.WriteLine("[params]   " + d);
            }
        }
    }
}