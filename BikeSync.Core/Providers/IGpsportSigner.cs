using System.Security.Cryptography;
using System.Text;
using Wasmtime;

namespace BikeSync.Core.Providers;

/// <summary>
/// Runs the public iGPSPORT web client signature module. The module is shipped by
/// iGPSPORT's own website and is embedded here so no network download is needed at runtime.
/// </summary>
public sealed class IGpsportSigner : IDisposable
{
    private const string ResourceName = "BikeSync.Core.edge_core.wasm";

    private readonly Engine _engine;
    private readonly Module _module;
    private readonly Store _store;
    private readonly Linker _linker;
    private readonly Instance _instance;
    private readonly Memory _memory;
    private readonly Func<int, int, int> _malloc;
    private readonly Function _init;
    private readonly Function _generate;
    private readonly object _gate = new();
    private bool _initialized;

    public IGpsportSigner()
    {
        _engine = new Engine();
        using (var stream = typeof(IGpsportSigner).Assembly.GetManifestResourceStream(ResourceName))
        {
            if (stream is null)
            {
                throw new InvalidOperationException("缺少 iGPSPORT 签名模块。");
            }

            _module = Module.FromStream(_engine, "edge_core.wasm", stream);
        }

        _store = new Store(_engine);
        _linker = new Linker(_engine);
        _linker.DefineFunction("wbg", "__wbindgen_init_externref_table", () => { });
        _linker.DefineFunction<int, int, string>(
            "wbg",
            "__wbindgen_cast_2241b6af4c4b2941",
            (_, _) => string.Empty);

        _instance = _linker.Instantiate(_store, _module);
        _memory = _instance.GetMemory("memory") ??
                  throw new InvalidOperationException("iGPSPORT 签名模块缺少内存。");
        _malloc = _instance.GetFunction<int, int, int>("__wbindgen_malloc") ??
                  throw new InvalidOperationException("iGPSPORT 签名模块缺少分配器。");
        _init = _instance.GetFunction("init_session_key") ??
                throw new InvalidOperationException("iGPSPORT 签名模块缺少初始化函数。");
        _generate = _instance.GetFunction("generate_signature") ??
                     throw new InvalidOperationException("iGPSPORT 签名模块缺少签名函数。");
        _instance.GetFunction("__wbindgen_start")?.Invoke();
    }

    public void Initialize(string secretBase64)
    {
        lock (_gate)
        {
            var secret = Convert.FromBase64String(secretBase64);
            var pointer = _malloc(secret.Length, 1);
            secret.CopyTo(_memory.GetSpan(pointer, secret.Length));
            var result = _init.Invoke(new ValueBox[] { pointer, secret.Length }) as object[];
            if (result is null || result.Length < 3 ||
                Convert.ToInt32(result[2]) != 0 || Convert.ToInt32(result[0]) == 0)
            {
                throw new InvalidOperationException("iGPSPORT 签名密钥被拒绝。");
            }

            _initialized = true;
        }
    }

    public string Sign(string method, string path, string timestamp, string nonce, string body)
    {
        lock (_gate)
        {
            if (!_initialized)
            {
                throw new InvalidOperationException("iGPSPORT 签名器尚未初始化。");
            }

            var arguments = new List<(int Pointer, int Length)>
            {
                Allocate(method),
                Allocate(path),
                Allocate(timestamp),
                Allocate(nonce),
                Allocate(body),
            };

            var boxes = new ValueBox[10];
            for (var i = 0; i < arguments.Count; i++)
            {
                boxes[i * 2] = arguments[i].Pointer;
                boxes[i * 2 + 1] = arguments[i].Length;
            }

            var result = _generate.Invoke(boxes) as object[];
            if (result is null || result.Length < 4 ||
                Convert.ToInt32(result[3]) != 0)
            {
                throw new InvalidOperationException("iGPSPORT 签名模块返回失败。");
            }

            var outputPointer = Convert.ToInt32(result[0]);
            var outputLength = Convert.ToInt32(result[1]);
            return Encoding.UTF8.GetString(_memory.GetSpan(outputPointer, outputLength));
        }
    }

    public static string Sha256Hex(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private (int Pointer, int Length) Allocate(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var pointer = _malloc(bytes.Length, 1);
        bytes.CopyTo(_memory.GetSpan(pointer, bytes.Length));
        return (pointer, bytes.Length);
    }

    public void Dispose()
    {
        _linker.Dispose();
        _store.Dispose();
        _module.Dispose();
        _engine.Dispose();
    }
}
