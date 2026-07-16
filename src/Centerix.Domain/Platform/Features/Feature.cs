namespace Centerix.Domain.Platform.Features;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Plans;

public class Feature : AuditableEntity<int>
{
    public string Code { get; private set; } = default!;
    public string Description { get; private set; } = default!;
    public string Module { get; private set; } = default!;

    private readonly List<PlanFeature> _planFeatures = [];
    public IReadOnlyList<PlanFeature> PlanFeatures => _planFeatures.AsReadOnly();

    private Feature() { }

    private Feature(int id, string code, string description, string module)
        : base(id)
    {
        Code = code;
        Description = description;
        Module = module;
    }

    public static Result<Feature> Create(int id, string code, string description, string module)
    {
        if (string.IsNullOrWhiteSpace(code))
            return FeatureErrors.CodeRequired;

        if (string.IsNullOrWhiteSpace(description))
            return FeatureErrors.DescriptionRequired;

        if (string.IsNullOrWhiteSpace(module))
            return FeatureErrors.ModuleRequired;

        return new Feature(id, code, description, module);
    }

    public Result<Updated> Update(string code, string description, string module)
    {
        if (string.IsNullOrWhiteSpace(code))
            return FeatureErrors.CodeRequired;

        if (string.IsNullOrWhiteSpace(description))
            return FeatureErrors.DescriptionRequired;

        if (string.IsNullOrWhiteSpace(module))
            return FeatureErrors.ModuleRequired;

        Code = code;
        Description = description;
        Module = module;

        return Result.Updated;
    }
}
