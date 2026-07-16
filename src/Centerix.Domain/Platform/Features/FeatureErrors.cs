namespace Centerix.Domain.Platform.Features;

using Centerix.Domain.Common.Results;

public static class FeatureErrors
{
    public static Error CodeRequired =>
        Error.Validation("Feature.Code_Required", "Feature code is required");

    public static Error DescriptionRequired =>
        Error.Validation("Feature.Description_Required", "Feature description is required");

    public static Error ModuleRequired =>
        Error.Validation("Feature.Module_Required", "Module is required");
}
