using Olam.Tests;

var failures = 0;
var scenarios = TranslationScenarios.Cases.ToArray();
foreach (var (name, check) in scenarios)
{
    try { check(); Console.WriteLine($"PASS {name}"); }
    catch (Exception error) { failures++; Console.Error.WriteLine($"FAIL {name}: {error}"); }
}
Console.WriteLine($"{scenarios.Length - failures}/{scenarios.Length} translation regression scenarios passed.");
Console.WriteLine("Offline runner: Microsoft OData parser and ASP.NET Core compatibility tests are excluded.");
return failures == 0 ? 0 : 1;
