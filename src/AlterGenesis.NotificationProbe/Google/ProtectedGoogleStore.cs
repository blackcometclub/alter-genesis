using System.IO;
using System.Security.Cryptography;
using System.Text;
using Google.Apis.Json;
using Google.Apis.Util.Store;

namespace AlterGenesis.NotificationProbe.Google;

// Every file contains a DPAPI ciphertext, including the client configuration and catalog.
internal sealed class ProtectedGoogleStore(string directory) : IDataStore
{
    private readonly string _directory = Path.GetFullPath(directory);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TINMOON.AlterGenesis.Google.v1");
    internal string PathFor<T>(string key) => Path.Combine(_directory,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(typeof(T).FullName + ":" + key))) + ".dpapi");
    internal bool Contains<T>(string key) => File.Exists(PathFor<T>(key));

    public async Task StoreAsync<T>(string key, T value)
    {
        var plain = Encoding.UTF8.GetBytes(NewtonsoftJsonSerializer.Instance.Serialize(value));
        byte[] encrypted;
        try { encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        await _gate.WaitAsync();
        var temporary = PathFor<T>(key) + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(_directory);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await stream.WriteAsync(encrypted);
                stream.Flush(flushToDisk: true);
            }
            var target = PathFor<T>(key);
            if (File.Exists(target)) File.Replace(temporary, target, null);
            else File.Move(temporary, target);
        }
        finally
        {
            try { File.Delete(temporary); }
            finally { _gate.Release(); }
        }
    }

    public async Task<T> GetAsync<T>(string key)
    {
        await _gate.WaitAsync();
        try
        {
            byte[] encrypted;
            try { encrypted = await File.ReadAllBytesAsync(PathFor<T>(key)); }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            { return default!; }
            var plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            try { return NewtonsoftJsonSerializer.Instance.Deserialize<T>(Encoding.UTF8.GetString(plain)); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteAsync<T>(string key)
    {
        await _gate.WaitAsync();
        try { File.Delete(PathFor<T>(key)); }
        finally { _gate.Release(); }
    }

    public async Task ClearAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (Directory.Exists(_directory))
                foreach (var file in Directory.EnumerateFiles(_directory, "*.dpapi")) File.Delete(file);
        }
        finally { _gate.Release(); }
    }
}
