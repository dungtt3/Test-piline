using MassTransit;
using Microsoft.Extensions.Options;

namespace Eaap.Infrastructure.Notifications;

/// <summary>
/// Configures the delivery endpoint's retry policy (build spec phase 4 section 6: 3 attempts,
/// 5s/25s/125s by default). Intervals come from config so tests can run them instantly.
/// </summary>
public class NotificationDeliveryConsumerDefinition : ConsumerDefinition<NotificationDeliveryConsumer>
{
    private readonly NotificationOptions _options;

    public NotificationDeliveryConsumerDefinition(IOptions<NotificationOptions> options)
    {
        _options = options.Value;
    }

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<NotificationDeliveryConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        if (_options.RetryIntervalMs > 0)
        {
            endpointConfigurator.UseMessageRetry(r =>
                r.Interval(_options.RetryLimit, TimeSpan.FromMilliseconds(_options.RetryIntervalMs)));
            return;
        }

        var intervals = (_options.RetryIntervalsSeconds.Length > 0
                ? _options.RetryIntervalsSeconds
                : [5, 25, 125])
            .Select(s => TimeSpan.FromSeconds(s))
            .ToArray();
        endpointConfigurator.UseMessageRetry(r => r.Intervals(intervals));
    }
}
