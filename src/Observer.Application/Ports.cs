namespace FullSpectrum.Observer.Application;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public interface IIdGenerator
{
    Guid NewId();
}
