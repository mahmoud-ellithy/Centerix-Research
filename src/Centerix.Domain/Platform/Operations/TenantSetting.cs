namespace Centerix.Domain.Platform.Operations;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;

public class TenantSetting : AuditableEntity<long>
{
    public string Key { get; private set; } = default!;
    public string Value { get; private set; } = default!;
    public string ValueType { get; private set; } = default!;

    private TenantSetting() { }

    private TenantSetting(
        long id,
        string key,
        string value,
        string valueType)
        : base(id)
    {
        Key = key;
        Value = value;
        ValueType = valueType;
    }

    public static Result<TenantSetting> Create(
        long id,
        string key,
        string value,
        string valueType)
    {
        if (string.IsNullOrWhiteSpace(key))
            return TenantSettingErrors.KeyRequired;

        if (string.IsNullOrWhiteSpace(value))
            return TenantSettingErrors.ValueRequired;

        if (string.IsNullOrWhiteSpace(valueType))
            return TenantSettingErrors.ValueTypeRequired;

        return new TenantSetting(id, key, value, valueType);
    }

    public Result<Updated> UpdateValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return TenantSettingErrors.ValueRequired;

        Value = value;
        return Result.Updated;
    }
}
