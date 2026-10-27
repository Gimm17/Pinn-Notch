using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using System.Threading;
using System.Threading.Tasks;

namespace VNotch.Services.Translation;

internal sealed partial class QwenTranslationEngine : ILocalTranslationEngine
{
    private const string NativeDll = "VNotch.Native.dll";

    [LibraryImport(NativeDll)]
    private static partial int vn_qwen_engine_requires_new_context(
        int beforeThreads,
        int afterThreads,
        int beforeBatchThreads,
        int afterBatchThreads,
        int beforeContextSize,
        int afterContextSize,
        int beforeBatchSize,
        int afterBatchSize,
        out byte outRequiresNew);

    [LibraryImport(NativeDll)]
    private static partial int vn_qwen_engine_compute_output_budget(
        int contextSize,
        int usedTokens,
        int reserveTokens,
        int inputTokens,
        int maxOutputTokens,
        out int outBudget,
        out byte outOk);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_qwen_engine_compute_trim_stop_sequence_end(
        ReadOnlySpan<char> output,
        int outputLen,
        out int outEnd);

    private TranslationOptions _options = new();
    private int MaxOutputTokens => _options.OutputTokens;
    private TranslationOptions _requestedOptions = new();
    public void Configure(TranslationOptions options) => Volatile.Write(ref _requestedOptions, options);

    internal static bool RequiresNewContext(TranslationOptions before, TranslationOptions after) =>
        vn_qwen_engine_requires_new_context(
            before.Threads, after.Threads,
            before.BatchThreads, after.BatchThreads,
            before.ContextSize, after.ContextSize,
            before.BatchSize, after.BatchSize,
            out byte req) == 0 && req != 0;

    private readonly TranslationModelStore _store;
    private readonly Task _previousDisposal;
    internal Task Disposal { get; private set; } = Task.CompletedTask;
    private DateTime _lastUsed = DateTime.UtcNow;
    private volatile bool _disposed;
    private volatile TranslationStage _stage = TranslationStage.Waiting;
    public TranslationStage Stage => _stage;

    internal QwenTranslationEngine(TranslationModelStore store, bool preferGpu = true, Task? previousDisposal = null)
    {
        _store = store;
        _previousDisposal = previousDisposal ?? Task.CompletedTask;
        int gpuLayers = preferGpu ? 36 : 0;
        _options = new TranslationOptions { GpuLayers = gpuLayers };
        _requestedOptions = _options;
    }

    public async Task PrepareAsync(CancellationToken cancellationToken)
    {
        _stage = TranslationStage.Waiting;
        await _previousDisposal.WaitAsync(cancellationToken).ConfigureAwait(false);
        _lastUsed = DateTime.UtcNow;
    }

    public Task<string> TranslateAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken) =>
        TranslateFittedAsync(new(text), sourceLanguage, targetLanguage, cancellationToken);

    public Task<string> TranslateFittedAsync(TranslationEngineInput input, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken)
    {
        if (_disposed) return Task.FromResult(string.Empty);
        _stage = TranslationStage.Translating;
        cancellationToken.ThrowIfCancellationRequested();
        _lastUsed = DateTime.UtcNow;
        return Task.FromResult(input.Text);
    }

    internal static string Glossary(string source, string target, bool enableHumanizer = true) => (source, target) switch
    {
        ("zh", "vi") => "Terminology: 发货 means gửi hàng or xuất hàng; 送达 means giao đến. 发货 never means xuất phát.",
        ("ja", "vi") => "Terminology: 発送 means gửi hàng or xuất hàng; 配達 means giao hàng. Keep those meanings distinct.",
        ("en", "vi") => enableHumanizer
            ? "Idioms and tone: live with an arrangement means chấp nhận; call it a day means nghỉ ở đây for today. Do not invent a meeting, work, or other setting when none is stated. Use natural, authentic Vietnamese without stiff robotic translationese."
            : "Idioms: live with an arrangement means chấp nhận; call it a day means nghỉ ở đây for today. Do not invent a meeting, work, or other setting when none is stated.",
        ("vi", "en") => enableHumanizer
            ? "Tone and idioms: In casual, conversational, or social media Vietnamese, colloquial address terms like 'sếp ơi', 'bác ơi', 'anh em' mean 'boss', 'bro', 'man', or 'folks' rather than formal 'sir'. Translate emotional and casual slang into natural, idiomatic conversational English."
            : "",
        (_, "en") => enableHumanizer
            ? "Tone and idioms: For casual, colloquial, or social media text, use natural conversational English rather than stiff or overly formal wording. Translate informal address terms naturally ('boss', 'bro', 'man') instead of formal 'sir'."
            : "",
        _ => ""
    };

    internal static string TrimStopSequence(string output)
    {
        if (string.IsNullOrEmpty(output)) return string.Empty;
        if (vn_qwen_engine_compute_trim_stop_sequence_end(output.AsSpan(), output.Length, out int end) == 0 && end >= 0 && end <= output.Length)
            return output[..end];
        return output;
    }

    internal static string ReadOutput(string output)
    {
        if (TryReadOutput(output, out string translation)) return translation;
        throw new TranslationException("translation.failed");
    }

    internal static bool TryReadOutput(string output, out string translation)
    {
        translation = "";
        try
        {
            using var json = JsonDocument.Parse(output);
            var root = json.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("translation", out var text) && text.ValueKind == JsonValueKind.String &&
                root.EnumerateObject().Count() == 1)
            { translation = text.GetString()!.Trim(); return true; }
        }
        catch (JsonException) { }
        return false;
    }

    public void ReleaseIdleResources()
    {
        _lastUsed = DateTime.UtcNow;
    }

    public void Dispose()
    {
        _disposed = true;
    }
}
