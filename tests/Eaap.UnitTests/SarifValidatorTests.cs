using Eaap.Sarif;

namespace Eaap.UnitTests;

public class SarifValidatorTests
{
    private static FileStream OpenFixture(string name) =>
        File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void Validate_MegaLinterSarif_ReturnsNoErrors()
    {
        using var stream = OpenFixture("megalinter-valid.sarif");
        var errors = SarifValidator.Validate(stream);
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_WrongVersion_ReturnsError()
    {
        using var stream = OpenFixture("sarif-wrong-version.sarif");
        var errors = SarifValidator.Validate(stream);
        Assert.Contains(errors, e => e.Contains("2.1.0"));
    }

    [Fact]
    public void Validate_MissingDriverName_ReturnsError()
    {
        using var stream = OpenFixture("sarif-missing-driver-name.sarif");
        var errors = SarifValidator.Validate(stream);
        Assert.Contains(errors, e => e.Contains("tool.driver.name"));
    }

    [Fact]
    public void Validate_NoRuns_ReturnsError()
    {
        using var stream = new MemoryStream("{\"version\":\"2.1.0\",\"runs\":[]}"u8.ToArray());
        var errors = SarifValidator.Validate(stream);
        Assert.Contains(errors, e => e.Contains("at least one run"));
    }
}
