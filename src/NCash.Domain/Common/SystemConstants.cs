namespace NCash.Domain.Common;

public static class SystemConstants
{
    public static readonly Guid TreasuryUserId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    public static readonly Guid TreasuryAccountId = Guid.Parse("00000000-0000-0000-0000-000000000002");
    public const string TreasuryAccountNumber = "ACC-SYSTEM-TREASURY";
    public const decimal InitialUserBalance = 100000m;
    public const string CurrencyBdt = "BDT";
}
