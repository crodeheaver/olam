namespace Olam.Tests;

public sealed class TranslationTests
{
    public static IEnumerable<object[]> Names => TranslationScenarios.Cases.Select(s => new object[] { s.Name });

    [Theory]
    [MemberData(nameof(Names))]
    public void Translate(string name) => TranslationScenarios.Cases.Single(s => s.Name == name).Check();
}
