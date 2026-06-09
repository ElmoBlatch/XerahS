#region License Information (GPL v3)

/*
    XerahS - The Avalonia UI implementation of ShareX
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SkiaSharp;
using XerahS.Common;
using XerahS.Platform.Abstractions;

namespace XerahS.Platform.Linux;

/// <summary>
/// Linux OCR via the Tesseract CLI (the <c>tesseract</c> binary), in the same shell-out style XerahS
/// already uses for capture (grim/slurp) and recording (ffmpeg). The image is written to a temp PNG and
/// <c>tesseract &lt;png&gt; stdout -l &lt;lang&gt;</c> is run; stdout is the recognized text. Requires the
/// <c>tesseract</c> package plus language data (e.g. <c>tesseract-data-eng</c>) to be installed.
/// </summary>
public class LinuxOcrService : IOcrService
{
    private const string TesseractExecutable = "tesseract";
    private const int RecognizeTimeoutMs = 30000;
    private const int ListLangsTimeoutMs = 5000;

    private readonly Func<string?> _resolveTesseractPath;
    private IReadOnlyList<string>? _cachedLanguageCodes;

    public LinuxOcrService() : this(null)
    {
    }

    internal LinuxOcrService(Func<string?>? resolveTesseractPath)
    {
        _resolveTesseractPath = resolveTesseractPath ?? (() => ResolveExecutableOnPath(TesseractExecutable));
    }

    public bool IsSupported => !string.IsNullOrEmpty(_resolveTesseractPath());

    public OcrLanguage[] GetAvailableLanguages()
    {
        return GetAvailableLanguageCodes().Select(MapCodeToLanguage).ToArray();
    }

    public async Task<OcrResult> RecognizeAsync(SKBitmap image, OcrOptions options)
    {
        string? tesseractPath = _resolveTesseractPath();
        if (string.IsNullOrEmpty(tesseractPath))
        {
            return Failure("Tesseract is not installed. Install the 'tesseract' package (and a language pack, " +
                "e.g. tesseract-data-eng) to enable OCR on Linux.");
        }

        if (image == null)
        {
            return Failure("No image provided for OCR.");
        }

        string langCode = ResolveLanguageCode(options.Language);
        string tempFile = Path.Combine(Path.GetTempPath(), $"xerahs_ocr_{Guid.NewGuid():N}.png");

        SKBitmap? scaled = null;
        try
        {
            SKBitmap working = image;
            if (options.ScaleFactor > 1f)
            {
                int width = Math.Max(1, (int)Math.Round(image.Width * options.ScaleFactor));
                int height = Math.Max(1, (int)Math.Round(image.Height * options.ScaleFactor));
                scaled = image.Resize(new SKImageInfo(width, height), new SKSamplingOptions(SKCubicResampler.Mitchell));
                if (scaled != null)
                {
                    working = scaled;
                }
            }

            using (var fs = File.Create(tempFile))
            {
                working.Encode(fs, SKEncodedImageFormat.Png, 100);
            }

            return await RunTesseractAsync(tesseractPath!, tempFile, langCode, options.SingleLine).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            DebugHelper.WriteException(ex, "LinuxOcrService: OCR failed");
            return Failure($"OCR failed: {ex.Message}");
        }
        finally
        {
            scaled?.Dispose();
            TryDelete(tempFile);
        }
    }

    private static async Task<OcrResult> RunTesseractAsync(string tesseractPath, string inputPath, string langCode, bool singleLine)
    {
        var psi = new ProcessStartInfo
        {
            FileName = tesseractPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string arg in BuildTesseractArguments(inputPath, langCode, singleLine))
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi);
        if (process == null)
        {
            return Failure("Failed to start tesseract.");
        }

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();

        bool exited = await Task.Run(() => process.WaitForExit(RecognizeTimeoutMs)).ConfigureAwait(false);
        if (!exited)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return Failure("tesseract timed out.");
        }

        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            return Failure(string.IsNullOrWhiteSpace(stderr)
                ? $"tesseract exited with code {process.ExitCode}."
                : stderr.Trim());
        }

        return new OcrResult { Text = stdout.Trim(), Success = true };
    }

    private string ResolveLanguageCode(string? language)
    {
        string mapped = MapLanguageToTesseractCode(language);
        IReadOnlyList<string> available = GetAvailableLanguageCodes();

        if (available.Count == 0 || available.Contains(mapped))
        {
            return mapped;
        }

        return available.Contains("eng") ? "eng" : available[0];
    }

    private IReadOnlyList<string> GetAvailableLanguageCodes()
    {
        if (_cachedLanguageCodes != null)
        {
            return _cachedLanguageCodes;
        }

        string? tesseractPath = _resolveTesseractPath();
        if (string.IsNullOrEmpty(tesseractPath))
        {
            _cachedLanguageCodes = Array.Empty<string>();
            return _cachedLanguageCodes;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = tesseractPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("--list-langs");

            using var process = Process.Start(psi);
            if (process == null)
            {
                _cachedLanguageCodes = Array.Empty<string>();
                return _cachedLanguageCodes;
            }

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(ListLangsTimeoutMs);

            // tesseract prints the language list to stdout on some builds and stderr on others.
            _cachedLanguageCodes = ParseListLangs(string.IsNullOrWhiteSpace(stdout) ? stderr : stdout);
        }
        catch (Exception ex)
        {
            DebugHelper.WriteException(ex, "LinuxOcrService: failed to list tesseract languages");
            _cachedLanguageCodes = Array.Empty<string>();
        }

        return _cachedLanguageCodes;
    }

    // ---------------------- testable pure helpers ----------------------

    internal static List<string> BuildTesseractArguments(string inputPath, string langCode, bool singleLine)
    {
        var args = new List<string> { inputPath, "stdout", "-l", langCode };
        if (singleLine)
        {
            // PSM 7: treat the image as a single text line.
            args.Add("--psm");
            args.Add("7");
        }

        return args;
    }

    /// <summary>
    /// Maps an XerahS language tag (e.g. "en", "en-US") to a Tesseract language code (e.g. "eng").
    /// Already-valid 3+ letter codes (eng, chi_sim, ...) pass through; unknown tags fall back to "eng".
    /// </summary>
    internal static string MapLanguageToTesseractCode(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return "eng";
        }

        string normalized = language.Trim().ToLowerInvariant();
        int separator = normalized.IndexOfAny(new[] { '-', '_' });
        string primary = separator > 0 ? normalized.Substring(0, separator) : normalized;

        // A 3+ letter token that isn't a known 2-letter tag is assumed to already be a Tesseract code
        // (e.g. "eng", "chi_sim"); Tesseract uses '_' separators, never '-'.
        if (normalized.Length >= 3 && !TwoLetterToTesseract.ContainsKey(primary))
        {
            return normalized.Replace('-', '_');
        }

        return TwoLetterToTesseract.TryGetValue(primary, out string? code) ? code : "eng";
    }

    /// <summary>Parses the output of <c>tesseract --list-langs</c> into language codes (skips header + osd).</summary>
    internal static IReadOnlyList<string> ParseListLangs(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return Array.Empty<string>();
        }

        var codes = new List<string>();
        foreach (string rawLine in output.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            // Skip the descriptive header and the OSD (orientation/script-detection) pseudo-language.
            if (line.StartsWith("List of available languages", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(line, "osd", StringComparison.OrdinalIgnoreCase) || line.Contains(' '))
            {
                continue;
            }

            codes.Add(line);
        }

        return codes;
    }

    internal static OcrLanguage MapCodeToLanguage(string code)
    {
        string display = TesseractCodeToDisplay.TryGetValue(code, out string? name) ? name : code;
        return new OcrLanguage(display, code);
    }

    private static readonly Dictionary<string, string> TwoLetterToTesseract = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "eng", ["de"] = "deu", ["fr"] = "fra", ["es"] = "spa", ["it"] = "ita",
        ["pt"] = "por", ["nl"] = "nld", ["ru"] = "rus", ["ja"] = "jpn", ["ko"] = "kor",
        ["zh"] = "chi_sim", ["ar"] = "ara", ["hi"] = "hin", ["pl"] = "pol", ["tr"] = "tur",
        ["sv"] = "swe", ["no"] = "nor", ["da"] = "dan", ["fi"] = "fin", ["cs"] = "ces",
        ["el"] = "ell", ["he"] = "heb", ["th"] = "tha", ["vi"] = "vie", ["uk"] = "ukr",
        ["ro"] = "ron", ["hu"] = "hun", ["id"] = "ind",
    };

    private static readonly Dictionary<string, string> TesseractCodeToDisplay = new(StringComparer.OrdinalIgnoreCase)
    {
        ["eng"] = "English", ["deu"] = "German", ["fra"] = "French", ["spa"] = "Spanish",
        ["ita"] = "Italian", ["por"] = "Portuguese", ["nld"] = "Dutch", ["rus"] = "Russian",
        ["jpn"] = "Japanese", ["kor"] = "Korean", ["chi_sim"] = "Chinese (Simplified)",
        ["chi_tra"] = "Chinese (Traditional)", ["ara"] = "Arabic", ["hin"] = "Hindi",
        ["pol"] = "Polish", ["tur"] = "Turkish", ["swe"] = "Swedish", ["nor"] = "Norwegian",
        ["dan"] = "Danish", ["fin"] = "Finnish", ["ces"] = "Czech", ["ell"] = "Greek",
        ["heb"] = "Hebrew", ["tha"] = "Thai", ["vie"] = "Vietnamese", ["ukr"] = "Ukrainian",
        ["ron"] = "Romanian", ["hun"] = "Hungarian", ["ind"] = "Indonesian",
    };

    private static string? ResolveExecutableOnPath(string executable)
    {
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
        {
            return null;
        }

        foreach (string dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(dir.Trim(), executable);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // Ignore malformed PATH entries.
            }
        }

        return null;
    }

    private static OcrResult Failure(string message) =>
        new() { Text = string.Empty, Success = false, ErrorMessage = message };

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // best effort
        }
    }
}
