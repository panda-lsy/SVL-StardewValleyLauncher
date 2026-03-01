using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace SVL.Core.IO
{
    /// <summary>
    /// .NET Framework 文件操作辅助类
    /// 提供 .NET Core+ 风格的异步文件 API
    /// </summary>
    public static class FileEx
    {
        public static Task<string> ReadAllTextAsync(string path)
        {
            return Task.Run(() => File.ReadAllText(path));
        }

        public static Task<string> ReadAllTextAsync(string path, Encoding encoding)
        {
            return Task.Run(() => File.ReadAllText(path, encoding));
        }

        public static Task WriteAllTextAsync(string path, string contents)
        {
            return Task.Run(() => File.WriteAllText(path, contents));
        }

        public static Task WriteAllTextAsync(string path, string contents, Encoding encoding)
        {
            return Task.Run(() => File.WriteAllText(path, contents, encoding));
        }

        public static Task WriteAllBytesAsync(string path, byte[] bytes)
        {
            return Task.Run(() => File.WriteAllBytes(path, bytes));
        }

        public static Task<byte[]> ReadAllBytesAsync(string path)
        {
            return Task.Run(() => File.ReadAllBytes(path));
        }

        public static Task<bool> ExistsAsync(string path)
        {
            return Task.Run(() => File.Exists(path));
        }
    }
}
