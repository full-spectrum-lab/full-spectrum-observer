using FullSpectrum.Observer.Application;

namespace FullSpectrum.Observer.EngineFacade;

public static class EngineFacadeComposition
{
    public static IObserverEngineFacade Create(EngineFacadeOptions options) => new PythonWorkerEngineFacade(options);
}
