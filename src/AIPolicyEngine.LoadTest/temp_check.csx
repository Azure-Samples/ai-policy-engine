using System;
using System.Reflection;
using System.Linq;

// Usage: dotnet script temp_check.csx -- <path-to-NBomber.dll>
var dllPath = Args.Count > 0 ? Args[0] : throw new InvalidOperationException("Pass the path to NBomber.dll as an argument.");
var asm = Assembly.LoadFrom(dllPath);
var types = asm.GetTypes().Where(t => t.Name.Contains("Report")).ToList();
foreach (var t in types) Console.WriteLine(t.FullName);
