using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace Road96FarsiAuto;

[BepInPlugin(PluginInfo.Guid, PluginInfo.Name, PluginInfo.Version)]
public sealed class Plugin : BasePlugin
{
    internal static new ManualLogSourceEx Log = null!;
    internal static TranslatorCore Translator = null!;
    internal static Harmony Harmony = null!;
    internal static ConfigEntry<bool> Enabled = null!;
    internal static ConfigEntry<bool> EnableRtl = null!;
    internal static ConfigEntry<bool> EnableFontFallback = null!;
    internal static ConfigEntry<bool> TranslateGameLocalizationMethods = null!;
    internal static ConfigEntry<bool> TranslateLegacyUiText = null!;
    internal static ConfigEntry<bool> TranslateTmpText = null!;
    internal static ConfigEntry<int> MaxParallel = null!;
    internal static ConfigEntry<int> RequestTimeoutSeconds = null!;
    internal static ConfigEntry<int> MaxTextLength = null!;
    internal static ConfigEntry<string> Endpoint = null!;
    internal static ConfigEntry<string> FallbackFonts = null!;
    internal static ConfigEntry<int> FallbackFontSize = null!;
    internal static ConfigEntry<bool> DebugLogging = null!;

    public override void Load()
    {
        Log = new ManualLogSourceEx(base.Log);

        Enabled = Config.Bind("General", "Enabled", true, "Enable runtime Persian translation.");
        EnableRtl = Config.Bind("Persian", "EnableRTL", true, "Apply Persian shaping and RTL-safe visual ordering.");
        EnableFontFallback = Config.Bind("Persian", "EnableFontFallback", true, "Try to add a Windows system font with Persian glyphs to TextMeshPro fallback fonts.");
        FallbackFonts = Config.Bind("Persian", "FallbackFonts", "Tahoma,Segoe UI,Noto Sans Arabic,Arial", "Fonts tried in order when Persian glyphs are missing.");
        FallbackFontSize = Config.Bind("Persian", "FallbackFontSize", 42, "Dynamic fallback font sampling size.");

        TranslateTmpText = Config.Bind("Hooks", "TranslateTMP", true, "Hook TextMeshPro TMP_Text.text.");
        TranslateLegacyUiText = Config.Bind("Hooks", "TranslateLegacyUI", true, "Hook UnityEngine.UI.Text.text.");
        TranslateGameLocalizationMethods = Config.Bind("Hooks", "TranslateLocalizationMethods", true, "Hook likely game/localization string-returning methods.");

        MaxParallel = Config.Bind("Translation", "MaxParallel", 6, "Maximum concurrent HTTP translation requests.");
        RequestTimeoutSeconds = Config.Bind("Translation", "RequestTimeoutSeconds", 8, "HTTP timeout per translation request.");
        MaxTextLength = Config.Bind("Translation", "MaxTextLength", 900, "Maximum source text length sent to the translation service.");
        Endpoint = Config.Bind("Translation", "Endpoint", "https://translate.googleapis.com/translate_a/single", "Google Translate-compatible endpoint.");
        DebugLogging = Config.Bind("Debug", "LogSkippedText", false, "Log rejected text candidates for debugging.");

        Translator = new TranslatorCore(
            Log,
            Endpoint.Value,
            "en",
            "fa",
            Math.Clamp(MaxParallel.Value, 1, 16),
            Math.Clamp(RequestTimeoutSeconds.Value, 2, 30),
            Math.Clamp(MaxTextLength.Value, 64, 4000));

        Harmony = new Harmony(PluginInfo.Guid);
        RuntimeHooks.Install(Harmony);

        Log.Info($"Road 96 Farsi Auto {PluginInfo.Version} loaded.");
        Log.Info("Runtime translation + protected tokens + async cache + Persian RTL shaping are active.");
    }

    public override bool Unload()
    {
        try { Harmony.UnpatchSelf(); } catch { }
        try { Translator.Dispose(); } catch { }
        return true;
    }
}

internal static class PluginInfo
{
    public const string Guid = "ridox.road96.farsiauto";
    public const string Name = "Road 96 Farsi Auto";
    public const string Version = "2.0.0";
}

internal sealed class ManualLogSourceEx
{
    private readonly BepInEx.Logging.ManualLogSource _inner;
    public ManualLogSourceEx(BepInEx.Logging.ManualLogSource inner) => _inner = inner;
    public void Info(object message) => _inner.LogInfo(message);
    public void Warning(object message) => _inner.LogWarning(message);
    public void Error(object message) => _inner.LogError(message);
    public void Debug(object message) => _inner.LogDebug(message);
}

internal static class RuntimeHooks
{
    private static readonly string[] TmpNames = { "TMPro.TMP_Text" };
    private static readonly string[] LegacyNames = { "UnityEngine.UI.Text" };
    private static readonly string[] LikelyTypeWords = { "dialogue", "localiz", "localise", "language", "localization", "localized", "text" };
    private static readonly string[] LikelyMethodWords = { "getlocalizedstring", "getstring", "gettext", "localize", "localized", "resolve", "settext", "totext", "buildtext", "formattext" };

    public static void Install(Harmony harmony)
    {
        if (Plugin.TranslateTmpText.Value)
            PatchTextSetter(harmony, TmpNames);

        if (Plugin.TranslateLegacyUiText.Value)
            PatchTextSetter(harmony, LegacyNames);

        if (Plugin.TranslateGameLocalizationMethods.Value)
            PatchLocalizationReturns(harmony);
    }

    private static void PatchTextSetter(Harmony harmony, IEnumerable<string> typeNames)
    {
        foreach (var typeName in typeNames)
        {
            try
            {
                var type = AccessTools.TypeByName(typeName);
                var setter = type?.GetProperty("text", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.SetMethod;
                if (setter == null) continue;
                harmony.Patch(setter, prefix: new HarmonyMethod(typeof(RuntimeHooks), nameof(TextSetterPrefix)));
                Plugin.Log.Info($"Hooked {typeName}.text");
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning($"Failed to hook {typeName}.text: {ex.Message}");
            }
        }
    }

    private static void PatchLocalizationReturns(Harmony harmony)
    {
        var patched = 0;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!IsGameAssembly(asm)) continue;
            Type[] types;
            try { types = asm.GetTypes(); } catch { continue; }

            foreach (var type in types)
            {
                var typeName = type.FullName ?? type.Name;
                if (!ContainsAny(typeName, LikelyTypeWords)) continue;
                MethodInfo[] methods;
                try { methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic); }
                catch { continue; }

                foreach (var method in methods)
                {
                    if (method.ReturnType != typeof(string)) continue;
                    if (method.IsAbstract || method.IsGenericMethodDefinition) continue;
                    if (!ContainsAny(method.Name, LikelyMethodWords)) continue;
                    if (method.Name.Equals("ToString", StringComparison.Ordinal)) continue;
                    if (method.GetMethodBody() == null && !type.Assembly.FullName!.Contains("Assembly-CSharp", StringComparison.OrdinalIgnoreCase)) continue;

                    try
                    {
                        harmony.Patch(method, postfix: new HarmonyMethod(typeof(RuntimeHooks), nameof(StringResultPostfix)));
                        patched++;
                        if (patched <= 40)
                            Plugin.Log.Info($"Hooked localization candidate: {type.FullName}.{method.Name}");
                    }
                    catch { }
                }
            }
        }

        Plugin.Log.Info($"Localization method scan complete. Patched {patched} candidate methods.");
    }

    private static bool IsGameAssembly(Assembly asm)
    {
        var n = asm.GetName().Name ?? string.Empty;
        if (n.StartsWith("System", StringComparison.OrdinalIgnoreCase) || n.StartsWith("mscorlib", StringComparison.OrdinalIgnoreCase) || n.StartsWith("BepInEx", StringComparison.OrdinalIgnoreCase) || n.StartsWith("0Harmony", StringComparison.OrdinalIgnoreCase) || n.StartsWith("Harmony", StringComparison.OrdinalIgnoreCase)) return false;
        return n.Contains("Assembly-CSharp", StringComparison.OrdinalIgnoreCase)
            || n.Contains("DialogueAssembly", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Road96", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Road 96", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Localization", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsAny(string value, IEnumerable<string> needles)
    {
        foreach (var n in needles)
            if (value.Contains(n, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static void TextSetterPrefix(object __instance, ref string __0)
    {
        if (!Plugin.Enabled.Value || TranslationGuard.IsApplying || string.IsNullOrEmpty(__0)) return;

        var original = __0;
        var cached = Plugin.Translator.TryGetCached(original);
        if (cached != null)
        {
            __0 = cached;
            TranslationGuard.MarkApplying();
            try { UiStyling.ApplyPersianPresentation(__instance, cached); }
            finally { TranslationGuard.UnmarkApplying(); }
            return;
        }

        Plugin.Translator.Queue(original, __instance);
    }

    private static void StringResultPostfix(ref string __result, object __instance)
    {
        if (!Plugin.Enabled.Value || TranslationGuard.IsApplying || string.IsNullOrEmpty(__result)) return;
        var cached = Plugin.Translator.TryGetCached(__result);
        if (cached != null)
        {
            __result = cached;
            return;
        }
        Plugin.Translator.Queue(__result, __instance);
    }
}

internal static class TranslationGuard
{
    [ThreadStatic] private static int _depth;
    public static bool IsApplying => _depth > 0;
    public static void MarkApplying() => _depth++;
    public static void UnmarkApplying() { if (_depth > 0) _depth--; }
}

internal sealed class TranslatorCore : IDisposable
{
    private readonly ManualLogSourceEx _log;
    private readonly string _endpoint;
    private readonly string _sourceLanguage;
    private readonly string _targetLanguage;
    private readonly int _maxTextLength;
    private readonly SemaphoreSlim _gate;
    private readonly HttpClient _http;
    private readonly SynchronizationContext? _unityContext;
    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentBag<WeakReference<object>>> _targets = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();
    private readonly string _cachePath;
    private readonly object _saveSync = new();
    private int _saveQueued;

    public TranslatorCore(ManualLogSourceEx log, string endpoint, string sourceLanguage, string targetLanguage, int parallel, int timeoutSeconds, int maxTextLength)
    {
        _log = log;
        _endpoint = endpoint.Trim();
        _sourceLanguage = sourceLanguage.Trim();
        _targetLanguage = targetLanguage.Trim();
        _maxTextLength = maxTextLength;
        _gate = new SemaphoreSlim(parallel, parallel);
        _http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(timeoutSeconds)
        };
        _unityContext = SynchronizationContext.Current;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Road96FarsiAuto/2.0");
        _cachePath = Path.Combine(Paths.ConfigPath, "Road96FarsiAuto.cache.json");
        LoadCache();
    }

    public string? TryGetCached(string source)
    {
        if (!TextProtector.ShouldTranslate(source, _maxTextLength)) return null;
        return _cache.TryGetValue(source, out var result) ? result : null;
    }

    public void Queue(string source, object? target)
    {
        if (!TextProtector.ShouldTranslate(source, _maxTextLength))
        {
            if (Plugin.DebugLogging.Value)
                _log.Debug($"Skipped candidate: {TrimForLog(source)}");
            return;
        }

        if (target != null)
        {
            var bag = _targets.GetOrAdd(source, _ => new ConcurrentBag<WeakReference<object>>());
            if (bag.Count < 32)
                bag.Add(new WeakReference<object>(target));
        }

        if (_cache.ContainsKey(source) || !_pending.TryAdd(source, 0)) return;
        _ = Task.Run(() => TranslateOneAsync(source), _cts.Token);
    }

    private async Task TranslateOneAsync(string original)
    {
        try
        {
            var protectedText = TextProtector.Protect(original, out var tokens);
            if (!TextProtector.ShouldTranslate(protectedText, _maxTextLength)) return;

            await _gate.WaitAsync(_cts.Token).ConfigureAwait(false);
            try
            {
                var translated = await RequestWithRetryAsync(protectedText, _cts.Token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(translated)) return;

                if (!TextProtector.TryRestoreAndValidate(translated, tokens, out var restored))
                {
                    _log.Warning($"Rejected translation because protected tokens were modified: {TrimForLog(original)}");
                    return;
                }

                restored = PersianText.Prepare(restored, Plugin.EnableRtl.Value);
                _cache[original] = restored;
                SaveCacheSoon();
                ApplyToTargets(original, restored);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.Debug($"Translation failed: {ex.Message}");
        }
        finally
        {
            _pending.TryRemove(original, out _);
            _targets.TryRemove(original, out _);
        }
    }

    private void ApplyToTargets(string source, string translated)
    {
        if (!_targets.TryGetValue(source, out var bag)) return;
        var targets = bag.ToArray();
        if (targets.Length == 0) return;

        try
        {
            void Apply()
            {
                foreach (var weak in targets)
                {
                    if (!weak.TryGetTarget(out var target) || target == null) continue;
                    TrySetText(target, translated);
                }
            }

            if (_unityContext != null)
                _unityContext.Post(_ => Apply(), null);
            else
                Apply();
        }
        catch (Exception ex)
        {
            _log.Debug($"Main-thread apply failed: {ex.Message}");
        }
    }

    private static void TrySetText(object target, string translated)
    {
        try
        {
            var prop = target.GetType().GetProperty("text", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop?.CanWrite != true) return;
            TranslationGuard.MarkApplying();
            try
            {
                prop.SetValue(target, translated);
                UiStyling.ApplyPersianPresentation(target, translated);
            }
            finally { TranslationGuard.UnmarkApplying(); }
        }
        catch { }
    }

    private async Task<string?> RequestWithRetryAsync(string text, CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var url = $"{_endpoint}?client=gtx&sl={Uri.EscapeDataString(_sourceLanguage)}&tl={Uri.EscapeDataString(_targetLanguage)}&dt=t&q={Uri.EscapeDataString(text)}";
                using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return ParseGoogleResponse(body);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                last = ex;
                if (attempt < 2)
                    await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), ct).ConfigureAwait(false);
            }
        }
        if (last != null) _log.Debug($"HTTP translation failed after retries: {last.Message}");
        return null;
    }

    private static string? ParseGoogleResponse(string body)
    {
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
        var sb = new StringBuilder();
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() == 0) continue;
            var first = row[0];
            if (first.ValueKind == JsonValueKind.Array && first.GetArrayLength() > 0 && first[0].ValueKind == JsonValueKind.String)
                sb.Append(first[0].GetString());
        }
        return sb.ToString();
    }

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(_cachePath)) return;
            var json = File.ReadAllText(_cachePath, Encoding.UTF8);
            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (data == null) return;
            foreach (var pair in data)
                if (!string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value)) _cache[pair.Key] = pair.Value;
            _log.Info($"Loaded {_cache.Count} cached translations.");
        }
        catch (Exception ex)
        {
            _log.Warning($"Cache load failed: {ex.Message}");
        }
    }

    private void SaveCacheSoon()
    {
        if (Interlocked.Exchange(ref _saveQueued, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(700, _cts.Token).ConfigureAwait(false);
                Dictionary<string, string> snapshot;
                lock (_saveSync) snapshot = _cache.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
                var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = false });
                var temp = _cachePath + ".tmp";
                File.WriteAllText(temp, json, Encoding.UTF8);
                File.Move(temp, _cachePath, true);
            }
            catch (OperationCanceledException) { }
            catch { }
            finally { Volatile.Write(ref _saveQueued, 0); }
        }, _cts.Token);
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _gate.Dispose(); } catch { }
        try { _http.Dispose(); } catch { }
        try { _cts.Dispose(); } catch { }
    }

    private static string TrimForLog(string value)
    {
        value = value.Replace("\r", " ").Replace("\n", " ");
        return value.Length <= 100 ? value : value[..100] + "…";
    }
}

internal static class TextProtector
{
    private const string MarkerPrefix = "ZXQ_R96P_";
    private const string MarkerSuffix = "_QXZ";
    private static readonly Regex TokenRegex = new($"{Regex.Escape(MarkerPrefix)}\\d{{4}}{Regex.Escape(MarkerSuffix)}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PlaceholderRegex = new(@"\{[^{}]{1,160}\}|\[[^\[\]]{1,160}\]|<[^<>]{1,300}>|(?<!\w)%[0-9]*[a-zA-Z]|\\[nrt\\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Protect(string input, out Dictionary<string, string> tokens)
    {
        var tokenMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var id = 0;
        var result = PlaceholderRegex.Replace(input, m =>
        {
            var key = $"{MarkerPrefix}{id++:0000}{MarkerSuffix}";
            tokenMap[key] = m.Value;
            return key;
        });

        result = Regex.Replace(result, @"(?i)\b(?:https?|ftp)://[^\s]+", m =>
        {
            var key = $"{MarkerPrefix}{id++:0000}{MarkerSuffix}";
            tokenMap[key] = m.Value;
            return key;
        }, RegexOptions.CultureInvariant);

        result = Regex.Replace(result, @"(?<!\w)(?:[A-Za-z]:[\\/]|\\\\)[^\s]+", m =>
        {
            var key = $"{MarkerPrefix}{id++:0000}{MarkerSuffix}";
            tokenMap[key] = m.Value;
            return key;
        }, RegexOptions.CultureInvariant);

        tokens = tokenMap;
        return result;
    }

    public static bool TryRestoreAndValidate(string translated, Dictionary<string, string> tokens, out string restored)
    {
        restored = translated;
        foreach (var token in tokens.Keys)
        {
            if (!restored.Contains(token, StringComparison.Ordinal)) return false;
        }
        foreach (var pair in tokens)
            restored = restored.Replace(pair.Key, pair.Value, StringComparison.Ordinal);
        return true;
    }

    public static bool ShouldTranslate(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (text.Length < 2 || text.Length > maxLength) return false;
        if (text.Contains(MarkerPrefix, StringComparison.Ordinal)) return false;
        if (ContainsPersianOrArabicLetters(text)) return false;
        if (LooksLikePureCode(text)) return false;

        var letterCount = text.Count(char.IsLetter);
        if (letterCount < 2) return false;

        // Very short UI labels are valid (Yes, No, Back, Continue), so don't require spaces.
        if (letterCount <= 5 && text.Count(c => c == ' ') == 0)
            return !LooksLikeIdentifier(text);

        return !LooksLikeIdentifier(text);
    }

    private static bool ContainsPersianOrArabicLetters(string text)
    {
        foreach (var c in text)
        {
            if ((c >= '\u0600' && c <= '\u06FF') || (c >= '\u0750' && c <= '\u077F') || (c >= '\u08A0' && c <= '\u08FF') || (c >= '\uFB50' && c <= '\uFDFF') || (c >= '\uFE70' && c <= '\uFEFF'))
                return true;
        }
        return false;
    }

    private static bool LooksLikeIdentifier(string text)
    {
        if (text.Contains("://", StringComparison.Ordinal) || text.Contains('\\')) return true;
        if (text.Contains('_') || text.Contains('#')) return true;
        if (text.All(c => char.IsLetterOrDigit(c) || c is '.' or ':' or '-' or '_'))
        {
            if (text.Any(char.IsDigit) && text.Any(char.IsLetter)) return true;
            if (text.Length > 20 && text.All(char.IsUpper)) return true;
        }
        return false;
    }

    private static bool LooksLikePureCode(string text)
    {
        if (text.Length > 100 && text.All(c => !char.IsWhiteSpace(c))) return true;
        var upper = text.Count(char.IsUpper);
        var letters = text.Count(char.IsLetter);
        if (text.Any(char.IsDigit) && !text.Any(char.IsWhiteSpace) && text.Any(char.IsLetter) && text.Length >= 6) return true;
        return false;
    }
}

internal static class PersianText
{
    private readonly record struct ShapeForm(int Isolated, int Final, int Initial, int Medial);

    private static readonly Dictionary<char, ShapeForm> Forms = new()
    {
        ['ا'] = new(0xFE8D, 0xFE8E, 0, 0),
        ['آ'] = new(0xFE81, 0xFE82, 0, 0),
        ['أ'] = new(0xFE83, 0xFE84, 0, 0),
        ['إ'] = new(0xFE87, 0xFE88, 0, 0),
        ['ؤ'] = new(0xFE85, 0xFE86, 0, 0),
        ['ء'] = new(0xFE80, 0, 0, 0),
        ['ب'] = new(0xFE8F, 0xFE90, 0xFE91, 0xFE92),
        ['پ'] = new(0xFB56, 0xFB57, 0xFB58, 0xFB59),
        ['ت'] = new(0xFE95, 0xFE96, 0xFE97, 0xFE98),
        ['ث'] = new(0xFE99, 0xFE9A, 0xFE9B, 0xFE9C),
        ['ج'] = new(0xFE9D, 0xFE9E, 0xFE9F, 0xFEA0),
        ['چ'] = new(0xFB7A, 0xFB7B, 0xFB7C, 0xFB7D),
        ['ح'] = new(0xFEA1, 0xFEA2, 0xFEA3, 0xFEA4),
        ['خ'] = new(0xFEA5, 0xFEA6, 0xFEA7, 0xFEA8),
        ['د'] = new(0xFEA9, 0xFEAA, 0, 0),
        ['ذ'] = new(0xFEAB, 0xFEAC, 0, 0),
        ['ر'] = new(0xFEAD, 0xFEAE, 0, 0),
        ['ز'] = new(0xFEAF, 0xFEB0, 0, 0),
        ['ژ'] = new(0xFB8A, 0xFB8B, 0, 0),
        ['س'] = new(0xFEB1, 0xFEB2, 0xFEB3, 0xFEB4),
        ['ش'] = new(0xFEB5, 0xFEB6, 0xFEB7, 0xFEB8),
        ['ص'] = new(0xFEB9, 0xFEBA, 0xFEBB, 0xFEBC),
        ['ض'] = new(0xFEBD, 0xFEBE, 0xFEBF, 0xFEC0),
        ['ط'] = new(0xFEC1, 0xFEC2, 0xFEC3, 0xFEC4),
        ['ظ'] = new(0xFEC5, 0xFEC6, 0xFEC7, 0xFEC8),
        ['ع'] = new(0xFEC9, 0xFECA, 0xFECB, 0xFECC),
        ['غ'] = new(0xFECD, 0xFECE, 0xFECF, 0xFED0),
        ['ف'] = new(0xFED1, 0xFED2, 0xFED3, 0xFED4),
        ['ق'] = new(0xFED5, 0xFED6, 0xFED7, 0xFED8),
        ['ک'] = new(0xFED9, 0xFEDA, 0xFEDB, 0xFEDC),
        ['گ'] = new(0xFB92, 0xFB93, 0xFB94, 0xFB95),
        ['ل'] = new(0xFEDD, 0xFEDE, 0xFEDF, 0xFEE0),
        ['م'] = new(0xFEE1, 0xFEE2, 0xFEE3, 0xFEE4),
        ['ن'] = new(0xFEE5, 0xFEE6, 0xFEE7, 0xFEE8),
        ['ه'] = new(0xFEE9, 0xFEEA, 0xFEEB, 0xFEEC),
        ['و'] = new(0xFEED, 0xFEEE, 0, 0),
        ['ی'] = new(0xFEF1, 0xFEF2, 0xFEF3, 0xFEF4),
        ['ى'] = new(0xFEEF, 0xFEF0, 0, 0),
    };

    public static string Prepare(string text, bool rtl)
    {
        text = text.Replace("ي", "ی", StringComparison.Ordinal)
                   .Replace("ى", "ی", StringComparison.Ordinal)
                   .Replace("ك", "ک", StringComparison.Ordinal)
                   .Replace("ۀ", "هٔ", StringComparison.Ordinal)
                   .Replace("ة", "ه", StringComparison.Ordinal);
        if (!rtl || !ContainsArabic(text)) return text;

        var parts = Regex.Split(text, "(<[^>]*>)", RegexOptions.CultureInvariant);
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].StartsWith('<') && parts[i].EndsWith('>')) continue;
            parts[i] = FixPlainSegment(parts[i]);
        }
        return string.Concat(parts);
    }

    private static bool ContainsArabic(string text)
    {
        foreach (var c in text)
            if (c >= '\u0600' && c <= '\u06FF') return true;
        return false;
    }

    private static string FixPlainSegment(string text)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
            lines[i] = FixLine(lines[i]);
        return string.Join("\n", lines);
    }

    private static string FixLine(string line)
    {
        if (!ContainsArabic(line)) return line;
        var tokens = Regex.Matches(line, @"\s+|\S+", RegexOptions.CultureInvariant).Select(m => m.Value).ToList();
        var words = tokens.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        if (words.Count == 0) return line;

        var fixedWords = new List<string>(words.Count);
        foreach (var word in words)
        {
            fixedWords.Add(ContainsArabic(word) ? ReverseGraphemes(ShapeArabic(word)) : word);
        }
        fixedWords.Reverse();

        var output = new StringBuilder(line.Length + 8);
        var wi = 0;
        foreach (var token in tokens)
        {
            if (string.IsNullOrWhiteSpace(token)) output.Append(token);
            else output.Append(fixedWords[wi++]);
        }
        return output.ToString();
    }

    private static string ShapeArabic(string input)
    {
        var chars = input.ToCharArray();
        var output = new StringBuilder(chars.Length);
        for (var i = 0; i < chars.Length; i++)
        {
            var ch = chars[i];
            if (!Forms.TryGetValue(ch, out var form))
            {
                output.Append(ch);
                continue;
            }

            var prev = FindPreviousJoinable(chars, i - 1);
            var next = FindNextJoinable(chars, i + 1);
            var joinPrev = prev >= 0 && CanJoinToNext(chars[prev]) && CanJoinToPrev(ch) && !BreakBetween(chars, prev, i);
            var joinNext = next < chars.Length && CanJoinToNext(ch) && CanJoinToPrev(chars[next]) && !BreakBetween(chars, i, next);

            var codePoint = form.Isolated;
            if (joinPrev && joinNext && form.Medial != 0) codePoint = form.Medial;
            else if (joinPrev && form.Final != 0) codePoint = form.Final;
            else if (joinNext && form.Initial != 0) codePoint = form.Initial;
            output.Append(char.ConvertFromUtf32(codePoint));
        }
        return output.ToString();
    }

    private static int FindPreviousJoinable(char[] chars, int start)
    {
        if (start < 0 || start >= chars.Length) return -1;
        return Forms.ContainsKey(chars[start]) ? start : -1;
    }

    private static int FindNextJoinable(char[] chars, int start)
    {
        if (start < 0 || start >= chars.Length) return chars.Length;
        return Forms.ContainsKey(chars[start]) ? start : -1;
    }

    private static bool BreakBetween(char[] chars, int left, int right)
        => chars[left] == '\u200C' || chars[right] == '\u200C';

    private static bool CanJoinToNext(char c) => Forms.TryGetValue(c, out var f) && (f.Initial != 0 || f.Medial != 0);
    private static bool CanJoinToPrev(char c) => Forms.TryGetValue(c, out var f) && (f.Final != 0 || f.Medial != 0);

    private static string ReverseGraphemes(string text)
    {
        var starts = StringInfo.ParseCombiningCharacters(text);
        var sb = new StringBuilder(text.Length);
        for (var i = starts.Length - 1; i >= 0; i--)
        {
            var start = starts[i];
            var end = i + 1 < starts.Length ? starts[i + 1] : text.Length;
            sb.Append(text, start, end - start);
        }
        return sb.ToString();
    }
}

internal static class UiStyling
{
    private static readonly ConcurrentDictionary<string, object> FontAssets = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object FontLock = new();

    public static void ApplyPersianPresentation(object target, string text)
    {
        if (!TextHasPersian(text)) return;
        try
        {
            var typeName = target.GetType().FullName ?? target.GetType().Name;
            if (typeName.Contains("TMP_Text", StringComparison.OrdinalIgnoreCase))
                ApplyTmpFallback(target);
            else if (typeName.Contains("UnityEngine.UI.Text", StringComparison.OrdinalIgnoreCase))
                ApplyLegacyFont(target);
        }
        catch { }
    }

    private static bool TextHasPersian(string text)
        => text.Any(c => c >= '\u0600' && c <= '\u06FF');

    private static void ApplyLegacyFont(object target)
    {
        if (!Plugin.EnableFontFallback.Value) return;
        var fontProp = target.GetType().GetProperty("font", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (fontProp?.CanWrite != true) return;
        var current = fontProp.GetValue(target);
        if (current == null) return;
        var hasCharacter = current.GetType().GetMethod("HasCharacter", new[] { typeof(char) });
        if (hasCharacter != null && (bool)hasCharacter.Invoke(current, new object[] { 'ی' })!) return;

        var fallback = GetUnityFont();
        if (fallback != null) fontProp.SetValue(target, fallback);
    }

    private static void ApplyTmpFallback(object target)
    {
        if (!Plugin.EnableFontFallback.Value) return;
        var fontProp = target.GetType().GetProperty("font", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var current = fontProp?.GetValue(target);
        if (current == null) return;

        var hasCharacters = current.GetType().GetMethod("HasCharacters", new[] { typeof(string) });
        if (hasCharacters != null)
        {
            try
            {
                var ok = (bool)hasCharacters.Invoke(current, new object[] { "یپچگ" })!;
                if (ok) return;
            }
            catch { }
        }

        var fallbackAsset = GetTmpFontAsset();
        if (fallbackAsset == null) return;

        var tableProp = current.GetType().GetProperty("fallbackFontAssetTable", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (tableProp?.CanRead == true)
        {
            try
            {
                var table = tableProp.GetValue(current);
                var add = table?.GetType().GetMethod("Add");
                if (add != null) add.Invoke(table, new[] { fallbackAsset });
            }
            catch { }
        }
    }

    private static object? GetUnityFont()
    {
        lock (FontLock)
        {
            var type = AccessTools.TypeByName("UnityEngine.Font");
            if (type == null) return null;
            var create = type.GetMethod("CreateDynamicFontFromOSFont", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string), typeof(int) }, null);
            if (create == null) return null;

            foreach (var name in Plugin.FallbackFonts.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (FontAssets.TryGetValue(name, out var cached)) return cached;
                try
                {
                    var font = create.Invoke(null, new object[] { name, Math.Clamp(Plugin.FallbackFontSize.Value, 16, 96) });
                    if (font != null)
                    {
                        FontAssets[name] = font;
                        return font;
                    }
                }
                catch { }
            }
        }
        return null;
    }

    private static object? GetTmpFontAsset()
    {
        const string cacheKey = "TMP_FONT_ASSET";
        if (FontAssets.TryGetValue(cacheKey, out var cached)) return cached;

        lock (FontLock)
        {
            if (FontAssets.TryGetValue(cacheKey, out cached)) return cached;
            var font = GetUnityFont();
            if (font == null) return null;
            var tmpType = AccessTools.TypeByName("TMPro.TMP_FontAsset");
            var unityFontType = AccessTools.TypeByName("UnityEngine.Font");
            if (tmpType == null || unityFontType == null) return null;

            foreach (var method in tmpType.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (!method.Name.Equals("CreateFontAsset", StringComparison.Ordinal) || method.GetParameters().Length == 0) continue;
                var ps = method.GetParameters();
                if (!ps[0].ParameterType.IsAssignableFrom(unityFontType)) continue;
                try
                {
                    var args = BuildFontAssetArguments(ps, font);
                    var asset = method.Invoke(null, args);
                    if (asset != null)
                    {
                        FontAssets[cacheKey] = asset;
                        return asset;
                    }
                }
                catch { }
            }
        }
        return null;
    }

    private static object?[] BuildFontAssetArguments(ParameterInfo[] ps, object font)
    {
        var args = new object?[ps.Length];
        args[0] = font;
        for (var i = 1; i < ps.Length; i++)
        {
            var p = ps[i];
            if (p.ParameterType == typeof(int))
            {
                args[i] = p.Name?.Contains("sampling", StringComparison.OrdinalIgnoreCase) == true ? Plugin.FallbackFontSize.Value : 9;
            }
            else if (p.ParameterType == typeof(bool)) args[i] = false;
            else if (p.ParameterType.IsEnum)
            {
                var names = Enum.GetNames(p.ParameterType);
                args[i] = names.Length > 0 ? Enum.Parse(p.ParameterType, names[0]) : Activator.CreateInstance(p.ParameterType);
            }
            else args[i] = null;
        }
        return args;
    }
}
