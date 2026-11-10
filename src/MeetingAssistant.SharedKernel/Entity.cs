namespace MeetingAssistant.SharedKernel;

/// <summary>
/// Сущность с идентичностью: две сущности равны, если совпали тип и <see cref="Id"/>.
/// </summary>
public abstract class Entity<TId>
    where TId : struct
{
    public TId Id { get; protected init; }

    public override bool Equals(object? obj) =>
        obj is Entity<TId> other
        && GetType() == other.GetType()
        && EqualityComparer<TId>.Default.Equals(Id, other.Id);

    public override int GetHashCode() => HashCode.Combine(GetType(), Id);
}
