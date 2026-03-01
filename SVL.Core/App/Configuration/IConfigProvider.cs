using System;
using System.Threading.Tasks;

namespace SVL.Core.App.Configuration;

public interface IConfigProvider
{
    Task<T> LoadAsync<T>(string key) where T : class, new();
    Task SaveAsync<T>(string key, T data) where T : class;
    Task<bool> ExistsAsync(string key);
    Task RemoveAsync(string key);
}
