using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Eaap.Infrastructure.Notifications;

namespace Eaap.UnitTests;

public class NotificationFormatterTests
{
    private static NotificationPayload Payload() => new(
        "GateFailed", Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Guid.Parse("22222222-2222-2222-2222-222222222222"), "my-repo", "GateFailed",
        "http://eaap/api/v1/jobs/11111111-1111-1111-1111-111111111111", DateTimeOffset.UnixEpoch);

    [Fact]
    public void Hmac_IsDeterministic_AndVerifiableWithTheSecret()
    {
        const string secret = "channel-secret";
        var body = NotificationFormatter.WebhookBody(Payload());

        var signature = HmacSigner.Sign(secret, body);

        Assert.StartsWith("sha256=", signature);
        // A receiver recomputes the same HMAC to verify authenticity.
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var expected = "sha256=" + Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        Assert.Equal(expected, signature);
    }

    [Fact]
    public void Hmac_DiffersWithWrongSecret()
    {
        var body = NotificationFormatter.WebhookBody(Payload());
        Assert.NotEqual(HmacSigner.Sign("right", body), HmacSigner.Sign("wrong", body));
    }

    [Fact]
    public void WebhookBody_CarriesTheCanonicalSchema()
    {
        using var doc = JsonDocument.Parse(NotificationFormatter.WebhookBody(Payload()));
        var root = doc.RootElement;
        Assert.Equal("GateFailed", root.GetProperty("event").GetString());
        Assert.Equal("my-repo", root.GetProperty("repositoryName").GetString());
        Assert.Equal("GateFailed", root.GetProperty("status").GetString());
        Assert.Contains("/api/v1/jobs/", root.GetProperty("summaryUrl").GetString());
    }

    [Fact]
    public void SlackBody_IsValidBlockKit_WithThreeFields()
    {
        using var doc = JsonDocument.Parse(NotificationFormatter.SlackBody(Payload()));
        var blocks = doc.RootElement.GetProperty("blocks");
        Assert.Equal(2, blocks.GetArrayLength()); // header + section
        var fields = blocks[1].GetProperty("fields");
        Assert.Equal(3, fields.GetArrayLength());
    }

    [Fact]
    public void EmailSubjectAndHtml_MentionEventAndRepository()
    {
        var payload = Payload();
        Assert.Contains("GateFailed", NotificationFormatter.EmailSubject(payload));
        Assert.Contains("my-repo", NotificationFormatter.EmailSubject(payload));
        Assert.Contains("my-repo", NotificationFormatter.EmailHtml(payload));
        Assert.Contains(payload.SummaryUrl, NotificationFormatter.EmailHtml(payload));
    }

    [Theory]
    [InlineData("https://github.com/acme/widgets.git", "widgets")]
    [InlineData("https://github.com/acme/widgets", "widgets")]
    [InlineData("git@github.com:acme/widgets.git", "widgets")]
    public void RepositoryName_IsDerivedFromCloneUrl(string cloneUrl, string expected)
    {
        var repo = new Eaap.Domain.Entities.Repository { CloneUrl = cloneUrl };
        Assert.Equal(expected, NotificationTriggerConsumer.RepositoryName(repo));
    }
}
