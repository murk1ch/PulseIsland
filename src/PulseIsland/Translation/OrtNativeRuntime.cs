using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using PulseIsland.Core;
using PulseIsland.Services;

namespace PulseIsland.Translation;

/// <summary>
/// Zero-dependency native ONNX Runtime (v1.20+) loader and C API (OrtApi) P/Invoke wrapper.
/// Strictly loads onnxruntime.dll from <portable-root>\models\_runtime\onnxruntime.dll (bypassing old Win10 System32 DLLs)
/// and can automatically bootstrap the official MIT-licensed Microsoft.ML.OnnxRuntime native library if missing.
/// </summary>
internal static class OrtNativeRuntime
{
    private const string NuGetOnnxPackageUrl =
        "https://api.nuget.org/v3-flatcontainer/microsoft.ml.onnxruntime/1.20.1/microsoft.ml.onnxruntime.1.20.1.nupkg";

    private const int ORT_LOGGING_LEVEL_ERROR = 3;
    private const int ORT_ENABLE_ALL = 99;
    private const int ORT_ARENA_ALLOCATOR = 1;
    private const int ORT_MEM_TYPE_DEFAULT = 0;
    private const int ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT = 1;
    private const int ONNX_TENSOR_ELEMENT_DATA_TYPE_INT64 = 7;

    private static readonly SemaphoreSlim _bootstrapLock = new(1, 1);
    private static IntPtr _dllHandle;
    private static IntPtr _ortApiPtr;
    private static OrtApiDelegates? _api;

    public static string RuntimeDirectory => Path.Combine(PortablePaths.ModelsDirectory, "_runtime");
    public static string RuntimeDllPath => Path.Combine(RuntimeDirectory, "onnxruntime.dll");

    public static bool IsRuntimeAvailable() => File.Exists(RuntimeDllPath);

    public static async Task<bool> EnsureRuntimeAvailableAsync(CancellationToken ct = default)
    {
        if (IsRuntimeAvailable())
            return true;

        await _bootstrapLock.WaitAsync(ct);
        try
        {
            if (IsRuntimeAvailable())
                return true;

            PortablePaths.EnsureDirectoriesExist();
            Directory.CreateDirectory(RuntimeDirectory);

            // Clean offline copy if the exact same MIT Microsoft.ML.OnnxRuntime binary is already cached in local tools
            string tempPkg = Path.Combine(PortablePaths.DownloadsCacheDirectory, "onnxruntime.1.20.1.nupkg");
            using var handler = new HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(3) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("PulseIsland/1.0");

            LogService.Info("Downloading Microsoft.ML.OnnxRuntime native runtime package...");
            using (var resp = await client.GetAsync(NuGetOnnxPackageUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                await using var netStream = await resp.Content.ReadAsStreamAsync(ct);
                await using var fs = new FileStream(tempPkg, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                await netStream.CopyToAsync(fs, ct);
            }

            using (var archive = ZipFile.OpenRead(tempPkg))
            {
                string ridPrefix = Environment.Is64BitProcess ? "runtimes/win-x64/native/" : "runtimes/win-x86/native/";
                foreach (var entry in archive.Entries)
                {
                    if (!entry.FullName.StartsWith(ridPrefix, StringComparison.OrdinalIgnoreCase) ||
                        string.IsNullOrEmpty(entry.Name))
                        continue;

                    string outPath = Path.Combine(RuntimeDirectory, entry.Name);
                    entry.ExtractToFile(outPath, overwrite: true);
                }
            }

            try { if (File.Exists(tempPkg)) File.Delete(tempPkg); } catch { }
            return IsRuntimeAvailable();
        }
        catch (Exception ex)
        {
            LogService.Error("Failed to download ONNX Runtime native library.", ex);
            return false;
        }
        finally
        {
            _bootstrapLock.Release();
        }
    }

    public static OrtApiDelegates EnsureApiLoaded()
    {
        if (_api is not null)
            return _api;

        if (!File.Exists(RuntimeDllPath))
            throw new FileNotFoundException($"ONNX Runtime native library not found at {RuntimeDllPath}");

        _dllHandle = NativeLibrary.Load(RuntimeDllPath);
        IntPtr getApiBasePtr = NativeLibrary.GetExport(_dllHandle, "OrtGetApiBase");
        var getApiBase = Marshal.GetDelegateForFunctionPointer<OrtGetApiBaseDelegate>(getApiBasePtr);
        IntPtr apiBasePtr = getApiBase();

        // OrtApiBase: [0] GetApi(uint version), [1] GetVersionString()
        IntPtr getApiPtr = Marshal.ReadIntPtr(apiBasePtr, 0);
        var getApi = Marshal.GetDelegateForFunctionPointer<GetApiDelegate>(getApiPtr);

        _ortApiPtr = getApi(17);
        if (_ortApiPtr == IntPtr.Zero)
            _ortApiPtr = getApi(14);
        if (_ortApiPtr == IntPtr.Zero)
            throw new InvalidOperationException("Unsupported onnxruntime.dll API version.");

        _api = new OrtApiDelegates(_ortApiPtr);
        return _api;
    }

    public sealed class OrtSessionHolder : IDisposable
    {
        private readonly OrtApiDelegates _api;
        private readonly IntPtr _env;
        private readonly IntPtr _memInfo;
        private readonly IntPtr _encoderSession;
        private readonly IntPtr _decoderSession;
        private bool _disposed;

        public OrtSessionHolder(string encoderPath, string decoderPath)
        {
            _api = EnsureApiLoaded();
            CheckStatus(_api, _api.CreateEnv(ORT_LOGGING_LEVEL_ERROR, "PulseIsland", out _env));
            CheckStatus(_api, _api.CreateCpuMemoryInfo(ORT_ARENA_ALLOCATOR, ORT_MEM_TYPE_DEFAULT, out _memInfo));

            CheckStatus(_api, _api.CreateSessionOptions(out IntPtr opts));
            try
            {
                _api.SetSessionGraphOptimizationLevel(opts, ORT_ENABLE_ALL);
                _api.SetIntraOpNumThreads(opts, Math.Clamp(Environment.ProcessorCount / 2, 1, 4));
                CheckStatus(_api, _api.CreateSession(_env, encoderPath, opts, out _encoderSession));
                CheckStatus(_api, _api.CreateSession(_env, decoderPath, opts, out _decoderSession));
            }
            finally
            {
                if (opts != IntPtr.Zero) _api.ReleaseSessionOptions(opts);
            }
        }

        public long[] RunGreedySeq2Seq(
            long[] inputIds,
            long[] attentionMask,
            long bosTokenId,
            long eosTokenId,
            int maxLength,
            CancellationToken ct)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(OrtSessionHolder));

            long[] inputShape = [1, inputIds.Length];
            var hIds = GCHandle.Alloc(inputIds, GCHandleType.Pinned);
            var hMask = GCHandle.Alloc(attentionMask, GCHandleType.Pinned);
            IntPtr idsTensor = IntPtr.Zero;
            IntPtr maskTensor = IntPtr.Zero;
            IntPtr encHiddenTensor = IntPtr.Zero;

            IntPtr pNameInputIds = Marshal.StringToCoTaskMemUTF8("input_ids");
            IntPtr pNameAttnMask = Marshal.StringToCoTaskMemUTF8("attention_mask");
            IntPtr pNameEncLastHidden = Marshal.StringToCoTaskMemUTF8("last_hidden_state");
            IntPtr pNameEncHiddenStates = Marshal.StringToCoTaskMemUTF8("encoder_hidden_states");
            IntPtr pNameEncAttnMask = Marshal.StringToCoTaskMemUTF8("encoder_attention_mask");
            IntPtr pNameLogits = Marshal.StringToCoTaskMemUTF8("logits");

            try
            {
                CheckStatus(_api, _api.CreateTensorWithDataAsOrtValue(
                    _memInfo,
                    hIds.AddrOfPinnedObject(),
                    (UIntPtr)(inputIds.Length * sizeof(long)),
                    inputShape,
                    (UIntPtr)inputShape.Length,
                    ONNX_TENSOR_ELEMENT_DATA_TYPE_INT64,
                    out idsTensor));

                CheckStatus(_api, _api.CreateTensorWithDataAsOrtValue(
                    _memInfo,
                    hMask.AddrOfPinnedObject(),
                    (UIntPtr)(attentionMask.Length * sizeof(long)),
                    inputShape,
                    (UIntPtr)inputShape.Length,
                    ONNX_TENSOR_ELEMENT_DATA_TYPE_INT64,
                    out maskTensor));

                IntPtr[] encInNames = [pNameInputIds, pNameAttnMask];
                IntPtr[] encInputs = [idsTensor, maskTensor];
                IntPtr[] encOutNames = [pNameEncLastHidden];
                IntPtr[] encOutputs = [IntPtr.Zero];

                CheckStatus(_api, _api.Run(
                    _encoderSession,
                    IntPtr.Zero,
                    encInNames,
                    encInputs,
                    (UIntPtr)2,
                    encOutNames,
                    (UIntPtr)1,
                    encOutputs));

                encHiddenTensor = encOutputs[0];

                var generated = new List<long> { bosTokenId };
                IntPtr[] decInNames = [pNameAttnMask == IntPtr.Zero ? IntPtr.Zero : pNameEncAttnMask, pNameInputIds, pNameEncHiddenStates];
                IntPtr[] decOutNames = [pNameLogits];

                for (int step = 0; step < maxLength; step++)
                {
                    ct.ThrowIfCancellationRequested();

                    long[] decIds = generated.ToArray();
                    long[] decShape = [1, decIds.Length];
                    var hDec = GCHandle.Alloc(decIds, GCHandleType.Pinned);
                    IntPtr decIdsTensor = IntPtr.Zero;
                    IntPtr[] decOutputs = [IntPtr.Zero];

                    try
                    {
                        CheckStatus(_api, _api.CreateTensorWithDataAsOrtValue(
                            _memInfo,
                            hDec.AddrOfPinnedObject(),
                            (UIntPtr)(decIds.Length * sizeof(long)),
                            decShape,
                            (UIntPtr)decShape.Length,
                            ONNX_TENSOR_ELEMENT_DATA_TYPE_INT64,
                            out decIdsTensor));

                        IntPtr[] decInputs = [maskTensor, decIdsTensor, encHiddenTensor];
                        CheckStatus(_api, _api.Run(
                            _decoderSession,
                            IntPtr.Zero,
                            decInNames,
                            decInputs,
                            (UIntPtr)3,
                            decOutNames,
                            (UIntPtr)1,
                            decOutputs));

                        long nextId = ExtractArgMaxFromLogits(decOutputs[0], decIds.Length);
                        if (nextId == eosTokenId)
                            break;

                        // Simple repetition guard against degenerate loops
                        if (generated.Count >= 6 &&
                            generated[^1] == nextId &&
                            generated[^2] == nextId &&
                            generated[^3] == nextId)
                        {
                            break;
                        }

                        generated.Add(nextId);
                    }
                    finally
                    {
                        if (decOutputs[0] != IntPtr.Zero) _api.ReleaseValue(decOutputs[0]);
                        if (decIdsTensor != IntPtr.Zero) _api.ReleaseValue(decIdsTensor);
                        if (hDec.IsAllocated) hDec.Free();
                    }
                }

                return generated.ToArray();
            }
            finally
            {
                if (encHiddenTensor != IntPtr.Zero) _api.ReleaseValue(encHiddenTensor);
                if (maskTensor != IntPtr.Zero) _api.ReleaseValue(maskTensor);
                if (idsTensor != IntPtr.Zero) _api.ReleaseValue(idsTensor);
                if (hMask.IsAllocated) hMask.Free();
                if (hIds.IsAllocated) hIds.Free();

                Marshal.FreeCoTaskMem(pNameInputIds);
                Marshal.FreeCoTaskMem(pNameAttnMask);
                Marshal.FreeCoTaskMem(pNameEncLastHidden);
                Marshal.FreeCoTaskMem(pNameEncHiddenStates);
                Marshal.FreeCoTaskMem(pNameEncAttnMask);
                Marshal.FreeCoTaskMem(pNameLogits);
            }
        }

        private long ExtractArgMaxFromLogits(IntPtr logitsValue, int seqLen)
        {
            IntPtr shapeInfo = IntPtr.Zero;
            try
            {
                CheckStatus(_api, _api.GetTensorTypeAndShape(logitsValue, out shapeInfo));
                CheckStatus(_api, _api.GetDimensionsCount(shapeInfo, out UIntPtr dimCount));
                int dimsLen = (int)dimCount;
                long[] dims = new long[dimsLen];
                CheckStatus(_api, _api.GetDimensions(shapeInfo, dims, dimCount));

                int vocabSize = (int)dims[^1];
                int stepOffset = Math.Max(0, (seqLen - 1) * vocabSize);

                CheckStatus(_api, _api.GetTensorMutableData(logitsValue, out IntPtr rawData));
                float[] logitsRow = new float[vocabSize];
                Marshal.Copy(IntPtr.Add(rawData, stepOffset * sizeof(float)), logitsRow, 0, vocabSize);

                long bestId = 0;
                float bestVal = float.NegativeInfinity;
                for (int v = 0; v < vocabSize; v++)
                {
                    float val = logitsRow[v];
                    if (val > bestVal)
                    {
                        bestVal = val;
                        bestId = v;
                    }
                }
                return bestId;
            }
            finally
            {
                if (shapeInfo != IntPtr.Zero) _api.ReleaseTensorTypeAndShapeInfo(shapeInfo);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_decoderSession != IntPtr.Zero) _api.ReleaseSession(_decoderSession);
            if (_encoderSession != IntPtr.Zero) _api.ReleaseSession(_encoderSession);
            if (_memInfo != IntPtr.Zero) _api.ReleaseMemoryInfo(_memInfo);
            if (_env != IntPtr.Zero) _api.ReleaseEnv(_env);
        }
    }

    /// <summary>
    /// General-purpose single-model float32 ONNX session wrapper (used by PP-OCRv5 DBNet detector & SVTR recognizers).
    /// Zero NuGet dependencies; runs natively on PortablePaths.ModelsDirectory\_runtime\onnxruntime.dll.
    /// </summary>
    public sealed class OrtFloatSession : IDisposable
    {
        private readonly OrtApiDelegates _api;
        private readonly IntPtr _env;
        private readonly IntPtr _memInfo;
        private readonly IntPtr _session;
        private readonly object _runLock = new();
        private bool _disposed;

        public OrtFloatSession(string modelPath, int threads = 0)
        {
            _api = EnsureApiLoaded();
            CheckStatus(_api, _api.CreateEnv(ORT_LOGGING_LEVEL_ERROR, "PulseIslandOcr", out _env));
            CheckStatus(_api, _api.CreateCpuMemoryInfo(ORT_ARENA_ALLOCATOR, ORT_MEM_TYPE_DEFAULT, out _memInfo));

            CheckStatus(_api, _api.CreateSessionOptions(out IntPtr opts));
            try
            {
                _api.SetSessionGraphOptimizationLevel(opts, ORT_ENABLE_ALL);
                int numThreads = threads > 0 ? threads : Math.Clamp(Environment.ProcessorCount / 2, 2, 6);
                _api.SetIntraOpNumThreads(opts, numThreads);
                CheckStatus(_api, _api.CreateSession(_env, modelPath, opts, out _session));
            }
            finally
            {
                if (opts != IntPtr.Zero) _api.ReleaseSessionOptions(opts);
            }
        }

        public (float[] Data, long[] Shape) RunFloat(
            float[] inputData,
            long[] inputShape,
            string inputName = "x",
            string outputName = "fetch_name_0")
        {
            lock (_runLock)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(OrtFloatSession));

                var hInput = GCHandle.Alloc(inputData, GCHandleType.Pinned);
                IntPtr inputTensor = IntPtr.Zero;
                IntPtr pInName = Marshal.StringToCoTaskMemUTF8(inputName);
                IntPtr pOutName = Marshal.StringToCoTaskMemUTF8(outputName);
                IntPtr[] outputs = [IntPtr.Zero];
                IntPtr shapeInfo = IntPtr.Zero;

                try
                {
                    CheckStatus(_api, _api.CreateTensorWithDataAsOrtValue(
                        _memInfo,
                        hInput.AddrOfPinnedObject(),
                        (UIntPtr)(inputData.Length * sizeof(float)),
                        inputShape,
                        (UIntPtr)inputShape.Length,
                        ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT,
                        out inputTensor));

                    IntPtr[] inNames = [pInName];
                    IntPtr[] inValues = [inputTensor];
                    IntPtr[] outNames = [pOutName];

                    CheckStatus(_api, _api.Run(
                        _session,
                        IntPtr.Zero,
                        inNames,
                        inValues,
                        (UIntPtr)1,
                        outNames,
                        (UIntPtr)1,
                        outputs));

                    CheckStatus(_api, _api.GetTensorTypeAndShape(outputs[0], out shapeInfo));
                    CheckStatus(_api, _api.GetDimensionsCount(shapeInfo, out UIntPtr dimCount));
                    long[] outShape = new long[(int)dimCount];
                    CheckStatus(_api, _api.GetDimensions(shapeInfo, outShape, dimCount));

                    long totalElements = 1;
                    for (int d = 0; d < outShape.Length; d++)
                        totalElements *= Math.Max(1, outShape[d]);

                    CheckStatus(_api, _api.GetTensorMutableData(outputs[0], out IntPtr rawData));
                    float[] result = new float[totalElements];
                    Marshal.Copy(rawData, result, 0, (int)totalElements);

                    return (result, outShape);
                }
                finally
                {
                    if (shapeInfo != IntPtr.Zero) _api.ReleaseTensorTypeAndShapeInfo(shapeInfo);
                    if (outputs[0] != IntPtr.Zero) _api.ReleaseValue(outputs[0]);
                    if (inputTensor != IntPtr.Zero) _api.ReleaseValue(inputTensor);
                    if (hInput.IsAllocated) hInput.Free();
                    Marshal.FreeCoTaskMem(pInName);
                    Marshal.FreeCoTaskMem(pOutName);
                }
            }
        }

        public void Dispose()
        {
            lock (_runLock)
            {
                if (_disposed) return;
                _disposed = true;
                if (_session != IntPtr.Zero) _api.ReleaseSession(_session);
                if (_memInfo != IntPtr.Zero) _api.ReleaseMemoryInfo(_memInfo);
                if (_env != IntPtr.Zero) _api.ReleaseEnv(_env);
            }
        }
    }

    private static void CheckStatus(OrtApiDelegates api, IntPtr status)
    {
        if (status == IntPtr.Zero)
            return;

        try
        {
            int code = api.GetErrorCode(status);
            IntPtr msgPtr = api.GetErrorMessage(status);
            string msg = Marshal.PtrToStringUTF8(msgPtr) ?? $"ORT error {code}";
            throw new InvalidOperationException(msg);
        }
        finally
        {
            api.ReleaseStatus(status);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr OrtGetApiBaseDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetApiDelegate(uint version);

    internal sealed class OrtApiDelegates
    {
        private readonly IntPtr _api;
        private readonly int _ptrSize = IntPtr.Size;

        public readonly GetErrorCodeDelegate GetErrorCode;
        public readonly GetErrorMessageDelegate GetErrorMessage;
        public readonly CreateEnvDelegate CreateEnv;
        public readonly CreateSessionDelegate CreateSession;
        public readonly RunDelegate Run;
        public readonly CreateSessionOptionsDelegate CreateSessionOptions;
        public readonly SetSessionGraphOptimizationLevelDelegate SetSessionGraphOptimizationLevel;
        public readonly SetIntraOpNumThreadsDelegate SetIntraOpNumThreads;
        public readonly CreateTensorWithDataAsOrtValueDelegate CreateTensorWithDataAsOrtValue;
        public readonly GetTensorMutableDataDelegate GetTensorMutableData;
        public readonly GetDimensionsCountDelegate GetDimensionsCount;
        public readonly GetDimensionsDelegate GetDimensions;
        public readonly GetTensorTypeAndShapeDelegate GetTensorTypeAndShape;
        public readonly CreateCpuMemoryInfoDelegate CreateCpuMemoryInfo;
        public readonly ReleaseEnvDelegate ReleaseEnv;
        public readonly ReleaseStatusDelegate ReleaseStatus;
        public readonly ReleaseMemoryInfoDelegate ReleaseMemoryInfo;
        public readonly ReleaseSessionDelegate ReleaseSession;
        public readonly ReleaseValueDelegate ReleaseValue;
        public readonly ReleaseTensorTypeAndShapeInfoDelegate ReleaseTensorTypeAndShapeInfo;
        public readonly ReleaseSessionOptionsDelegate ReleaseSessionOptions;

        public OrtApiDelegates(IntPtr apiPtr)
        {
            _api = apiPtr;
            // Verified vtable indices from OrtApi 1.20.1
            GetErrorCode = GetFn<GetErrorCodeDelegate>(1);
            GetErrorMessage = GetFn<GetErrorMessageDelegate>(2);
            CreateEnv = GetFn<CreateEnvDelegate>(3);
            CreateSession = GetFn<CreateSessionDelegate>(7);
            Run = GetFn<RunDelegate>(9);
            CreateSessionOptions = GetFn<CreateSessionOptionsDelegate>(10);
            SetSessionGraphOptimizationLevel = GetFn<SetSessionGraphOptimizationLevelDelegate>(23);
            SetIntraOpNumThreads = GetFn<SetIntraOpNumThreadsDelegate>(24);
            CreateTensorWithDataAsOrtValue = GetFn<CreateTensorWithDataAsOrtValueDelegate>(49);
            GetTensorMutableData = GetFn<GetTensorMutableDataDelegate>(51);
            GetDimensionsCount = GetFn<GetDimensionsCountDelegate>(61);
            GetDimensions = GetFn<GetDimensionsDelegate>(62);
            GetTensorTypeAndShape = GetFn<GetTensorTypeAndShapeDelegate>(65);
            CreateCpuMemoryInfo = GetFn<CreateCpuMemoryInfoDelegate>(69);
            ReleaseEnv = GetFn<ReleaseEnvDelegate>(92);
            ReleaseStatus = GetFn<ReleaseStatusDelegate>(93);
            ReleaseMemoryInfo = GetFn<ReleaseMemoryInfoDelegate>(94);
            ReleaseSession = GetFn<ReleaseSessionDelegate>(95);
            ReleaseValue = GetFn<ReleaseValueDelegate>(96);
            ReleaseTensorTypeAndShapeInfo = GetFn<ReleaseTensorTypeAndShapeInfoDelegate>(99);
            ReleaseSessionOptions = GetFn<ReleaseSessionOptionsDelegate>(100);
        }

        private T GetFn<T>(int index) where T : Delegate
            => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(_api, index * _ptrSize));

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int GetErrorCodeDelegate(IntPtr status);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate IntPtr GetErrorMessageDelegate(IntPtr status);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate IntPtr CreateEnvDelegate(int logLevel, [MarshalAs(UnmanagedType.LPUTF8Str)] string logId, out IntPtr env);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate IntPtr CreateSessionDelegate(IntPtr env, [MarshalAs(UnmanagedType.LPWStr)] string modelPath, IntPtr options, out IntPtr session);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate IntPtr RunDelegate(
            IntPtr session,
            IntPtr runOptions,
            [In] IntPtr[] inputNames,
            [In] IntPtr[] inputs,
            UIntPtr inputLen,
            [In] IntPtr[] outputNames,
            UIntPtr outputLen,
            [Out] IntPtr[] outputs);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate IntPtr CreateSessionOptionsDelegate(out IntPtr options);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate IntPtr SetSessionGraphOptimizationLevelDelegate(IntPtr options, int level);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate IntPtr SetIntraOpNumThreadsDelegate(IntPtr options, int threads);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate IntPtr CreateTensorWithDataAsOrtValueDelegate(
            IntPtr info,
            IntPtr pData,
            UIntPtr dataLen,
            [In] long[] shape,
            UIntPtr shapeLen,
            int type,
            out IntPtr value);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate IntPtr GetTensorMutableDataDelegate(IntPtr value, out IntPtr data);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate IntPtr GetDimensionsCountDelegate(IntPtr info, out UIntPtr count);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate IntPtr GetDimensionsDelegate(IntPtr info, [Out] long[] dimValues, UIntPtr dimValuesLen);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate IntPtr GetTensorTypeAndShapeDelegate(IntPtr value, out IntPtr info);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate IntPtr CreateCpuMemoryInfoDelegate(int allocatorType, int memType, out IntPtr info);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void ReleaseEnvDelegate(IntPtr env);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void ReleaseStatusDelegate(IntPtr status);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void ReleaseMemoryInfoDelegate(IntPtr info);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void ReleaseSessionDelegate(IntPtr session);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void ReleaseValueDelegate(IntPtr value);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void ReleaseTensorTypeAndShapeInfoDelegate(IntPtr info);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void ReleaseSessionOptionsDelegate(IntPtr options);
    }
}
