using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace SVL.Core.App;

public class LifecycleContext
{
    private readonly Dictionary<Type, object> _services = [];
    private readonly List<(Type Service, LifecycleState State)> _registeredServices = [];

    public T GetService<T>() where T : class
    {
        return _services.TryGetValue(typeof(T), out var service) ? service as T : null;
    }

    public void RegisterService(Type serviceType, LifecycleState state)
    {
        _registeredServices.Add((serviceType, state));
    }

    public IEnumerable<(Type Service, LifecycleState State)> GetRegisteredServices() => _registeredServices;
}

public static class Lifecycle
{
    private static LifecycleContext _context;
    private static readonly List<object> _services = [];

    public static LifecycleContext GetContext(object service)
    {
        return _context;
    }

    public static async Task StartAsync(LifecycleState targetState)
    {
        _context = new LifecycleContext();
        var services = FindServices(targetState);

        foreach (var service in services)
        {
            _services.Add(service);
            _context.RegisterService(service.GetType(), targetState);

            var lifecycleAttr = service.GetType().GetCustomAttribute<LifecycleServiceAttribute>();
            if (lifecycleAttr != null)
            {
                var lifecycleScope = service.GetType().GetCustomAttribute<LifecycleScopeAttribute>();
                Console.WriteLine($"[Lifecycle] Starting service: {lifecycleScope?.Name ?? service.GetType().Name}");
            }

            var startMethod = FindLifecycleMethod(service, typeof(LifecycleStartAttribute));
            if (startMethod != null)
            {
                var result = startMethod.Invoke(service, null);
                if (result is Task task)
                {
                    await task;
                }
            }
        }
    }

    public static async Task StopAsync(LifecycleState targetState)
    {
        var servicesToStop = _services.FindAll(s =>
        {
            var lifecycleAttr = s.GetType().GetCustomAttribute<LifecycleServiceAttribute>();
            return lifecycleAttr?.State >= targetState;
        }).OrderByDescending(s =>
        {
            var attr = s.GetType().GetCustomAttribute<LifecycleServiceAttribute>();
            return attr?.Priority ?? 0;
        });

        foreach (var service in servicesToStop)
        {
            var stopMethod = FindLifecycleMethod(service, typeof(LifecycleStopAttribute));
            if (stopMethod != null)
            {
                var result = stopMethod.Invoke(service, null);
                if (result is Task task)
                {
                    await task;
                }

                var lifecycleScope = service.GetType().GetCustomAttribute<LifecycleScopeAttribute>();
                Console.WriteLine($"[Lifecycle] Stopped service: {lifecycleScope?.Name ?? service.GetType().Name}");
            }

            _services.Remove(service);
        }

        _context = null;
    }

    private static List<object> FindServices(LifecycleState state)
    {
        var services = new List<object>();
        var assembly = Assembly.GetExecutingAssembly();

        foreach (var type in assembly.GetTypes())
        {
            var attr = type.GetCustomAttribute<LifecycleServiceAttribute>();
            if (attr != null && attr.State == state)
            {
                var instance = Activator.CreateInstance(type);
                services.Add(instance);
            }
        }

        return services.OrderBy(s =>
        {
            var attr = s.GetType().GetCustomAttribute<LifecycleServiceAttribute>();
            return attr?.Priority ?? 0;
        }).ToList();
    }

    private static MethodInfo FindLifecycleMethod(object service, Type attributeType)
    {
        return service.GetType().GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.GetCustomAttribute(attributeType) != null);
    }
}
