namespace EntregasApi.Services;

public static class SecurityRateLimitPolicies
{
    public const string AuthPassword = "auth-password";
    public const string AuthSession = "auth-session";
    public const string PublicTokenRead = "public-token-read";
    public const string PublicTokenWrite = "public-token-write";
    public const string DriverTokenRead = "driver-token-read";
    public const string DriverTokenWrite = "driver-token-write";
    public const string DriverTokenHighFrequency = "driver-token-high-frequency";
    public const string PushSubscribe = "push-subscribe";
    public const string LinkEvents = "link-events";
    public const string Webhook = "webhook";
}
