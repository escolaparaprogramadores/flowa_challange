namespace Flowa.Commons.Entities;

public abstract class Entity
{
    public Guid Id { get; }

    protected Entity() => Id = Guid.CreateVersion7();

    protected Entity(Guid id) => Id = id;

    public override bool Equals(object? otherObject) =>
        otherObject is Entity otherEntity && otherEntity.GetType() == GetType() && otherEntity.Id == Id;

    public override int GetHashCode() => Id.GetHashCode();
}
