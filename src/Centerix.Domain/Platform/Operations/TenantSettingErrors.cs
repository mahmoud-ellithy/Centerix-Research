namespace Centerix.Domain.Platform.Operations;

using Centerix.Domain.Common.Results;

public static class TenantSettingErrors
{
    public static Error KeyRequired =>
        Error.Validation("TenantSetting.Key_Required", "Setting key is required");

    public static Error ValueRequired =>
        Error.Validation("TenantSetting.Value_Required", "Setting value is required");

    public static Error ValueTypeRequired =>
        Error.Validation("TenantSetting.ValueType_Required", "Value type is required");
}
