namespace Centerix.Domain.Teachers.Subjects;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;

public class Subject : AuditableEntity<int>
{
    public string Name { get; private set; } = default!;
    public int StageId { get; private set; }

    private Subject() { }

    private Subject(int id, string name, int stageId)
        : base(id)
    {
        Name = name;
        StageId = stageId;
    }

    public static Result<Subject> Create(int id, string name, int stageId)
    {
        if (string.IsNullOrWhiteSpace(name))
            return SubjectErrors.NameRequired;

        if (stageId <= 0)
            return SubjectErrors.StageIdRequired;

        return new Subject(id, name.Trim(), stageId);
    }

    public Result<Updated> Update(string name, int stageId)
    {
        if (string.IsNullOrWhiteSpace(name))
            return SubjectErrors.NameRequired;

        if (stageId <= 0)
            return SubjectErrors.StageIdRequired;

        Name = name.Trim();
        StageId = stageId;

        return Result.Updated;
    }
}