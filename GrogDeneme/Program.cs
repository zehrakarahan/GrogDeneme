// See https://aka.ms/new-console-template for more information
using Microsoft.Extensions.Configuration;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

Console.WriteLine("Hello, World!");

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

var configuration = new ConfigurationBuilder()
    .AddUserSecrets<Program>()
    .Build();

var apiKey = configuration["Groq:ApiKey"];
if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.WriteLine("Groq:ApiKey ayarı bulunamadı.");
    return;
}

Console.Write("Düzenlenecek proje klasörü: ");
var inputPath = Console.ReadLine()?.Trim().Trim('"');

if (string.IsNullOrWhiteSpace(inputPath) ||
    !Directory.Exists(inputPath))
{
    Console.WriteLine("Klasör bulunamadı.");
    return;
}

var workspace = Path.TrimEndingDirectorySeparator(
    Path.GetFullPath(inputPath));

try
{
    CheckLinks(workspace);
}
catch (Exception ex)
{
    Console.WriteLine(ex.Message);
    return;
}

using var http = new HttpClient
{
    BaseAddress = new Uri("https://api.groq.com/openai/v1/"),
    Timeout = TimeSpan.FromSeconds(120)
};

http.DefaultRequestHeaders.Authorization =
    new AuthenticationHeaderValue("Bearer", apiKey);

var tools = JsonNode.Parse("""
[
  {
    "type": "function",
    "function": {
      "name": "list_files",
      "description": "Projedeki izin verilen kod dosyalarını listeler.",
      "parameters": {
        "type": "object",
        "properties": {},
        "additionalProperties": false
      }
    }
  },
  {
    "type": "function",
    "function": {
      "name": "read_file",
      "description": "Kod dosyasını satır aralığıyla okur. Varsayılan 80 satır. Devamı için start_line kullan.",
      "parameters": {
        "type": "object",
        "properties": {
          "path": { "type": "string" },
          "start_line": { "type": "integer", "minimum": 1 },
          "line_count": { "type": "integer", "minimum": 1, "maximum": 120 }
        },
        "required": ["path"],
        "additionalProperties": false
      }
    }
  },
  {
    "type": "function",
    "function": {
      "name": "write_file",
      "description": "Onay alınarak dosya oluşturur veya değiştirir. content dosyanın TAM yeni içeriğidir.",
      "parameters": {
        "type": "object",
        "properties": {
          "path": { "type": "string" },
          "content": { "type": "string" }
        },
        "required": ["path", "content"],
        "additionalProperties": false
      }
    }
  }
]
""")!;

((JsonArray)tools).Add(JsonNode.Parse("""
{
  "type": "function",
  "function": {
    "name": "search_file",
    "description": "Proje içindeki bir kod dosyasında metin arar ve satır numaralarıyla eşleşmeleri döndürür.",
    "parameters": {
      "type": "object",
      "properties": {
        "path": { "type": "string", "description": "Proje köküne göre dosya yolu." },
        "query": { "type": "string", "minLength": 1 },
        "max_results": { "type": "integer", "minimum": 1, "maximum": 50 }
      },
      "required": ["path", "query"],
      "additionalProperties": false
    }
  }
}
"""));
((JsonArray)tools).Add(JsonNode.Parse("""
{
  "type": "function",
  "function": {
    "name": "search_project",
    "description": "Proje genelinde dosya yollarında veya dosya içeriğinde büyük/küçük harf duyarsız düz metin arar. Dosya yolu bilinmiyorsa önce bunu kullan. Sonuçlar sınırlıdır.",
    "parameters": {
      "type": "object",
      "properties": {
        "query": { "type": "string", "minLength": 1 },
        "mode": { "type": "string", "enum": ["path", "content"], "description": "path: dosya yolunda, content: dosya içeriğinde arar. Varsayılan content." },
        "max_results": { "type": "integer", "minimum": 1, "maximum": 30 }
      },
      "required": ["query"],
      "additionalProperties": false
    }
  }
}
"""));
var messages = new JsonArray
{
    new JsonObject
    {
        ["role"] = "system",
        ["content"] = """
        Sen bir C# kod düzenleme asistanısın. Türkçe yanıt ver.
        İlgili dosya bilinmiyorsa önce search_project ile dosya yolunda veya içerikte ara. Tüm dosyaları gereksiz yere listeleme.
        Yalnızca tools listesinde tanımlı araçları kullan. Dosyada metin aramak için search_file kullan.
        Araç dosya yollarını seçilen proje köküne göre göreli ver.
        Mevcut dosyaları değiştirmeden önce read_file ile oku.
        İstenen değişiklikleri write_file aracını kullanarak uygula.
        write_file için dosyanın tamamını gönder, kısaltma kullanma.
        Kullanıcı değişikliği reddederse aynı işlemde tekrar ısrar etme.
        Araç başarı bildirmeden dosyanın değiştirildiğini söyleme.
        Derleme ve çalıştırma aracın yok; test edildiğini iddia etme.
        Gizli bilgi veya API anahtarı isteme ve dosyalara ekleme.
        Aynı aramayı tekrar etme. Eşleşmelerin satırlarını read_file start_line ve line_count ile incele.
        Eski araç sonuçları kısaltılabilir. Eksik içeriği uydurma; gerekiyorsa yeniden oku.
        """
    }
};

Console.WriteLine($"\nHedef: {workspace}");
Console.WriteLine("İsteğini yaz. Çıkış: /exit | Geçmişi temizle: /clear");

while (true)
{
    Console.Write("\nSen: ");
    var question = Console.ReadLine();

    if (question is null ||
        question.Equals("/exit", StringComparison.OrdinalIgnoreCase))
        break;

    if (string.IsNullOrWhiteSpace(question))
        continue;

    if (question.Trim().Equals("/clear", StringComparison.OrdinalIgnoreCase))
    {
        while (messages.Count > 1)
            messages.RemoveAt(messages.Count - 1);
        Console.WriteLine("Konuşma geçmişi temizlendi. Dosyalar değişmedi.");
        continue;
    }
    // Hatalı veya yarım kalan turu geçmişten kaldırabilmek için.
    var turnStart = messages.Count;

    messages.Add(new JsonObject
    {
        ["role"] = "user",
        ["content"] = question
    });

    try
    {
        var completed = false;

        for (var step = 0; step < 60; step++)
        {
            var request = new JsonObject
            {
                ["model"] = "openai/gpt-oss-20b",
                ["messages"] = BuildContext(),
                ["tools"] = tools.DeepClone(),
                ["tool_choice"] = "auto",
                ["max_completion_tokens"] = 2048,
                ["stream"] = false
            };

            var json = await SendWithRetryAsync(request);

            var result = JsonNode.Parse(json)
                ?? throw new Exception("API yanıtı boş.");

            var choice = result["choices"]?[0]
                ?? throw new Exception("Yanıt bulunamadı.");

            if (choice["finish_reason"]?.GetValue<string>() == "length")
                throw new Exception(
                    "Yanıt token sınırında kesildi. Daha küçük bir işlem iste.");

            var message = choice["message"] as JsonObject
                ?? throw new Exception("Mesaj bulunamadı.");

            var calls = message["tool_calls"] as JsonArray;

            var assistantMessage = new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = message["content"]?.DeepClone()
            };

            if (calls is { Count: > 0 })
                assistantMessage["tool_calls"] = calls.DeepClone();

            messages.Add(assistantMessage);

            if (calls is not { Count: > 0 })
            {
                Console.WriteLine(
                    $"\nAgent: {message["content"]?.GetValue<string>()}");

                completed = true;
                break;
            }

            foreach (var call in calls)
            {
                var name = call!["function"]!["name"]!.GetValue<string>();
                var arguments = call["function"]!["arguments"]!
                    .GetValue<string>();

                Console.WriteLine($"[Araç: {name}]");

                string output;

                try
                {
                    output = ExecuteTool(name, arguments);
                }
                catch (Exception ex)
                {
                    output = JsonSerializer.Serialize(new
                    {
                        success = false,
                        error = ex.Message
                    });
                }

                messages.Add(new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = call["id"]!.GetValue<string>(),
                    ["content"] = output
                });
            }
        }

        if (!completed)
            throw new Exception("Agent tur sınırına ulaştı.");
    }
    catch (Exception ex)
    {
        while (messages.Count > turnStart)
            messages.RemoveAt(messages.Count - 1);

        Console.WriteLine($"Hata: {ex.Message}");
        Console.WriteLine(
            "Daha önce onaylayıp kaydettiğin dosya değişiklikleri korunur.");
    }
}

JsonArray BuildContext()
{
    var context = (JsonArray)messages.DeepClone();
    // Önce eski, tamamlanmış kullanıcı turlarını kaldır.
    while (context.Count > 2 && context.ToJsonString().Length > 14000)
    {
        var nextUser = -1;
        for (var i = 2; i < context.Count; i++)
            if (context[i]?["role"]?.GetValue<string>() == "user")
            {
                nextUser = i;
                break;
            }
        if (nextUser < 0) break;
        for (var i = 1; i < nextUser; i++) context.RemoveAt(1);
    }
    // Araç kimliklerini koru, eski sonuçların metnini açıkça kısalt.
    var toolIndexes = Enumerable.Range(0, context.Count)
        .Where(i => context[i]?["role"]?.GetValue<string>() == "tool").ToArray();
    foreach (var index in toolIndexes.Take(Math.Max(0, toolIndexes.Length - 2)))
    {
        var content = context[index]?["content"]?.GetValue<string>();
        if (content?.Length > 400)
            context[index]!["content"] = content[..400] + " [Eski sonuç kısaltıldı; gerekirse yeniden oku.]";
    }
    // En eski araç turunu çağrısı ve TÜM sonuçlarıyla birlikte çıkar.
    while (context.ToJsonString().Length > 18000)
    {
        var removed = false;
        for (var i = 1; i < context.Count - 1; i++)
        {
            if (context[i]?["tool_calls"] is not JsonArray) continue;
            var end = i + 1;
            while (end < context.Count && context[end]?["role"]?.GetValue<string>() == "tool") end++;
            if (end == context.Count) continue; // Son araç turunu koru.
            var count = end - i;
            for (var j = 0; j < count; j++) context.RemoveAt(i);
            removed = true;
            break;
        }
        if (!removed) break;
    }
    return context;
}
async Task<string> SendWithRetryAsync(JsonObject request)
{
    const int maxAttempts = 12;

    for (var attempt = 0; attempt < maxAttempts; attempt++)
    {
        using var response = await http.PostAsJsonAsync("chat/completions", request);
        var json = await response.Content.ReadAsStringAsync();

        if (response.IsSuccessStatusCode)
            return json;

        if (response.StatusCode != System.Net.HttpStatusCode.TooManyRequests)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest &&
                json.Contains("output_parse_failed", StringComparison.Ordinal) && attempt < 2)
            {
                Console.WriteLine("[Model yanıtı okunamadı; otomatik tekrar deneniyor.]");
                await Task.Delay(TimeSpan.FromSeconds(2));
                continue;
            }
            throw new HttpRequestException($"API hatası ({(int)response.StatusCode}): {json}");
        }

        string errorMessage;
        try
        {
            errorMessage = JsonNode.Parse(json)?["error"]?["message"]?.GetValue<string>() ?? json;
        }
        catch (JsonException)
        {
            errorMessage = json;
        }

        // Tek isteğin kendisi kotadan büyükse beklemek sorunu çözmez.
        var sizes = System.Text.RegularExpressions.Regex.Match(
            errorMessage, @"Limit\s+(\d+).*?Requested\s+(\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (sizes.Success &&
            long.TryParse(sizes.Groups[1].Value, out var tokenLimit) &&
            long.TryParse(sizes.Groups[2].Value, out var requestedTokens) &&
            requestedTokens > tokenLimit)
        {
            throw new Exception(
                "Tek istek token kotasını aşıyor. /clear ile geçmişi temizleyip daha küçük bir dosya veya görev seç. " + errorMessage);
        }

        if (attempt == maxAttempts - 1)
            throw new Exception("Kota beklemesine rağmen istek tamamlanamadı. Daha sonra tekrar dene. " + errorMessage);

        var wait = response.Headers.RetryAfter?.Delta;
        if (wait is null && response.Headers.RetryAfter?.Date is { } retryDate)
            wait = retryDate - DateTimeOffset.UtcNow;

        if (wait is null)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                errorMessage, @"try again in\s+([0-9.]+)s",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (match.Success && double.TryParse(match.Groups[1].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds))
                wait = TimeSpan.FromSeconds(seconds);
        }

        wait ??= TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
        if (wait.Value > TimeSpan.FromMinutes(10))
            throw new Exception("Kota için uzun bekleme gerekiyor. Daha sonra tekrar dene. " + errorMessage);

        var delay = TimeSpan.FromSeconds(Math.Max(1, wait.Value.TotalSeconds) + 1);
        Console.WriteLine($"[Kota doldu: {delay.TotalSeconds:F1} saniye bekleniyor. Deneme {attempt + 2}/{maxAttempts}]");
        await Task.Delay(delay);
    }

    throw new InvalidOperationException("İstek tamamlanamadı.");
}
string ExecuteTool(string name, string arguments)
{
    using var document = JsonDocument.Parse(arguments);
    var args = document.RootElement;

    if (name == "list_files")
    {
        var files = ListFiles(workspace)
            .Take(60)
            .ToArray();

        return JsonSerializer.Serialize(files);
    }

    if (name == "search_project")
    {
        var query = args.GetProperty("query").GetString();
        if (string.IsNullOrWhiteSpace(query) || query.Length > 200)
            throw new Exception("Arama metni 1-200 karakter olmalı.");

        var mode = args.TryGetProperty("mode", out var modeValue)
            ? modeValue.GetString() : "content";
        if (mode != "path" && mode != "content")
            throw new Exception("mode path veya content olmalı.");

        var maxResults = 5;
        if (args.TryGetProperty("max_results", out var limit))
        {
            if (!limit.TryGetInt32(out maxResults))
                throw new Exception("max_results tam sayı olmalı.");
            maxResults = Math.Clamp(maxResults, 1, 10);
        }

        var matches = new List<object>();
        var filesScanned = 0;
        var skippedFiles = 0;
        var truncated = false;
        var timer = System.Diagnostics.Stopwatch.StartNew();

        foreach (var relative in ListFiles(workspace))
        {
            if (filesScanned >= 30000 || timer.Elapsed > TimeSpan.FromSeconds(60))
            {
                truncated = true;
                break;
            }

            filesScanned++;
            try
            {
                var filePath = ResolvePath(relative);
                if (mode == "path")
                {
                    if (!relative.Replace((char)92, (char)47).Contains(
                        query.Replace((char)92, (char)47), StringComparison.OrdinalIgnoreCase))
                        continue;
                    matches.Add(new { path = relative });
                }
                else
                {
                    if (new FileInfo(filePath).Length > 2_000_000)
                    {
                        skippedFiles++;
                        continue;
                    }

                    var lineNumber = 0;
                    foreach (var line in File.ReadLines(filePath))
                    {
                        lineNumber++;
                        if (timer.Elapsed > TimeSpan.FromSeconds(60))
                        {
                            truncated = true;
                            break;
                        }
                        if (!line.Contains(query, StringComparison.OrdinalIgnoreCase))
                            continue;

                        matches.Add(new
                        {
                            path = relative,
                            line = lineNumber,
                            text = line.Length > 300 ? line[..300] : line
                        });
                        if (matches.Count > maxResults)
                            break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skippedFiles++;
            }

            if (matches.Count > maxResults || truncated)
            {
                truncated = true;
                break;
            }
        }

        return JsonSerializer.Serialize(new
        {
            query, mode, filesScanned, skippedFiles, truncated,
            matches = matches.Take(maxResults)
        });
    }
    if (name != "read_file" && name != "write_file" && name != "search_file")
        throw new Exception("İzin verilmeyen araç.");

    var relativePath = args.GetProperty("path").GetString()
        ?? throw new Exception("Dosya yolu gerekli.");

    var fullPath = ResolvePath(relativePath);

    if (name == "search_file")
    {
        var query = args.GetProperty("query").GetString();
        if (string.IsNullOrWhiteSpace(query))
            throw new Exception("Aranacak metin boş olamaz.");

        if (!File.Exists(fullPath))
            throw new Exception("Dosya bulunamadı.");

        if (new FileInfo(fullPath).Length > 2_000_000)
            throw new Exception("Dosya arama için çok büyük.");

        var maxResults = 5;
        if (args.TryGetProperty("max_results", out var limit))
        {
            if (!limit.TryGetInt32(out maxResults))
                throw new Exception("max_results tam sayı olmalı.");
            maxResults = Math.Clamp(maxResults, 1, 10);
        }

        var matches = File.ReadLines(fullPath)
            .Select((text, index) => new { line = index + 1, text })
            .Where(item => item.text.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(maxResults + 1)
            .ToArray();

        return JsonSerializer.Serialize(new
        {
            path = relativePath,
            query,
            truncated = matches.Length > maxResults,
            matches = matches.Take(maxResults).Select(item => new
            {
                item.line,
                text = item.text.Length > 500 ? item.text[..500] : item.text
            })
        });
    }
    if (name == "read_file")
    {
        if (!File.Exists(fullPath))
            throw new Exception("Dosya bulunamadı.");

        var startLine = args.TryGetProperty("start_line", out var startValue)
            ? startValue.GetInt32() : 1;
        var lineCount = args.TryGetProperty("line_count", out var countValue)
            ? countValue.GetInt32() : 80;
        if (startLine < 1 || lineCount < 1)
            throw new Exception("Satır değerleri pozitif olmalı.");
        lineCount = Math.Min(lineCount, 120);
        if (new FileInfo(fullPath).Length > 2_000_000)
            throw new Exception("Dosya okuma için çok büyük.");
        var lines = File.ReadLines(fullPath).Skip(startLine - 1).Take(lineCount + 1).ToArray();
        var selected = new List<object>();
        var characters = 0;
        var consumed = 0;
        foreach (var line in lines.Take(lineCount))
        {
            if (characters + line.Length > 6000)
                break;
            selected.Add(new { line = startLine + consumed, text = line });
            characters += line.Length;
            consumed++;
        }
        if (consumed == 0 && lines.Length > 0)
            throw new Exception("Tek satır çok uzun; search_file ile hedef metni ara.");
        return JsonSerializer.Serialize(new
        {
            path = relativePath, startLine,
            next_start_line = startLine + consumed,
            has_more = lines.Length > consumed,
            lines = selected
        });
    }

    var content = args.GetProperty("content").GetString()
        ?? throw new Exception("Dosya içeriği gerekli.");

    if (Encoding.UTF8.GetByteCount(content) > 60_000)
        throw new Exception("Yeni içerik bu demo için çok büyük.");

    Console.WriteLine($"\nDosya: {relativePath}");

    if (File.Exists(fullPath))
    {
        if (new FileInfo(fullPath).Length > 60_000)
            throw new Exception("Mevcut dosya bu demo için çok büyük.");

        Console.WriteLine("----- MEVCUT İÇERİK -----");
        Console.WriteLine(File.ReadAllText(fullPath));
    }

    Console.WriteLine("----- YENİ İÇERİK -----");
    Console.WriteLine(content);
    Console.Write("\nDosyaya kaydedilsin mi? E/H: ");

    if (!string.Equals(
        Console.ReadLine()?.Trim(), "E",
        StringComparison.OrdinalIgnoreCase))
    {
        return """{"success":false,"message":"Kullanıcı reddetti."}""";
    }

    // Yazmadan önce yolu tekrar kontrol et.
    fullPath = ResolvePath(relativePath);

    Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
    File.WriteAllText(fullPath, content, new UTF8Encoding(false));

    return JsonSerializer.Serialize(new
    {
        success = true,
        message = "Dosya kaydedildi.",
        path = relativePath
    });
}

string ResolvePath(string relativePath)
{
    if (string.IsNullOrWhiteSpace(relativePath) ||
        Path.IsPathRooted(relativePath) ||
        relativePath.Contains(':'))
    {
        throw new Exception("Proje içindeki göreli dosya yolunu kullan.");
    }

    var fullPath = Path.GetFullPath(
        Path.Combine(workspace, relativePath));

    if (!fullPath.StartsWith(
        workspace + Path.DirectorySeparatorChar,
        StringComparison.OrdinalIgnoreCase))
    {
        throw new Exception("Proje klasörü dışına erişilemez.");
    }

    var normalized = Path.GetRelativePath(workspace, fullPath);
    var parts = normalized.Split(
        new[] { '/', '\\' },
        StringSplitOptions.RemoveEmptyEntries);

    if (parts.Any(IsBlockedName) || !IsAllowedFile(fullPath))
        throw new Exception("Bu dosyaya erişim izni yok.");

    CheckLinks(fullPath);
    return fullPath;
}

IEnumerable<string> ListFiles(string directory)
{
    foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
    {
        if (IsBlockedName(Path.GetFileName(entry)))
            continue;

        var attributes = File.GetAttributes(entry);

        if (attributes.HasFlag(FileAttributes.ReparsePoint))
            continue;

        if (attributes.HasFlag(FileAttributes.Directory))
        {
            foreach (var file in ListFiles(entry))
                yield return file;
        }
        else if (IsAllowedFile(entry))
        {
            yield return Path.GetRelativePath(workspace, entry);
        }
    }
}

static bool IsBlockedName(string name)
{
    return name.StartsWith('.') ||
        new[] { "bin", "obj", "node_modules", "secrets.json" }
            .Contains(name, StringComparer.OrdinalIgnoreCase) ||
        name.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase);
}

static bool IsAllowedFile(string path)
{
    return new[] { ".cs", ".csproj", ".razor", ".html", ".css", ".js", ".ts" }
        .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}

static void CheckLinks(string path)
{
    // Bağlantı/junction üzerinden farklı klasöre geçilmesini engeller.
    var current = path;

    while (!string.IsNullOrEmpty(current))
    {
        if ((File.Exists(current) || Directory.Exists(current)) &&
            File.GetAttributes(current)
                .HasFlag(FileAttributes.ReparsePoint))
        {
            throw new Exception(
                "Sembolik bağlantı veya junction içeren yol kullanılamaz.");
        }

        current = Path.GetDirectoryName(current);
    }
}