using System;
using System.IO;
using System.Reflection;

internal static class Program
{
    public static int Main(string[] args)
    {
        string managed = Environment.GetEnvironmentVariable("PUCK_MANAGED_DIR") ?? @"C:\Program Files (x86)\Steam\steamapps\common\Puck\Puck_Data\Managed";
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) => {
            string path = Path.Combine(managed, new AssemblyName(e.Name).Name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        try { return (int)typeof(Program).Assembly.GetType("OPLStats_Stats.Stats").GetMethod("RunPerformanceRegression").Invoke(null, new object[] { args }); }
        catch (Exception e) { Console.WriteLine(e); return 1; }
    }
}
