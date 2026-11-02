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
      "description": "Proje köküne göre verilen kod dosyasını okur.",
      "parameters": {
        "type": "object",
        "properties": {
          "path": { "type": "string" }
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

var messages = new JsonArray
{
    new JsonObject
    {
        ["role"] = "system",
        ["content"] = """
        Sen bir C# kod düzenleme asistanısın. Türkçe yanıt ver.
        İşlem yapmadan önce list_files ile dosyaları keşfet.
        Mevcut dosyaları değiştirmeden önce read_file ile oku.
        İstenen değişiklikleri write_file aracını kullanarak uygula.
        write_file için dosyanın tamamını gönder, kısaltma kullanma.
        Kullanıcı değişikliği reddederse aynı işlemde tekrar ısrar etme.
        Araç başarı bildirmeden dosyanın değiştirildiğini söyleme.
        Derleme ve çalıştırma aracın yok; test edildiğini iddia etme.
        Gizli bilgi veya API anahtarı isteme ve dosyalara ekleme.
        """
    }
};

Console.WriteLine($"\nHedef: {workspace}");
Console.WriteLine("İsteğini yaz. Çıkış: /exit");

while (true)
{
    Console.Write("\nSen: ");
    var question = Console.ReadLine();

    if (question is null ||
        question.Equals("/exit", StringComparison.OrdinalIgnoreCase))
        break;

    if (string.IsNullOrWhiteSpace(question))
        continue;

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

        for (var step = 0; step < 12; step++)
        {
            var request = new JsonObject
            {
                ["model"] = "openai/gpt-oss-20b",
                ["messages"] = messages.DeepClone(),
                ["tools"] = tools.DeepClone(),
                ["tool_choice"] = "auto",
                ["max_completion_tokens"] = 8192,
                ["stream"] = false
            };

            using var response = await http.PostAsJsonAsync(
                "chat/completions", request);

            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"API hatası ({(int)response.StatusCode}): {json}");
            }

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

string ExecuteTool(string name, string arguments)
{
    using var document = JsonDocument.Parse(arguments);
    var args = document.RootElement;

    if (name == "list_files")
    {
        var files = ListFiles(workspace)
            .Take(300)
            .ToArray();

        return JsonSerializer.Serialize(files);
    }

    if (name != "read_file" && name != "write_file")
        throw new Exception("İzin verilmeyen araç.");

    var relativePath = args.GetProperty("path").GetString()
        ?? throw new Exception("Dosya yolu gerekli.");

    var fullPath = ResolvePath(relativePath);

    if (name == "read_file")
    {
        if (!File.Exists(fullPath))
            throw new Exception("Dosya bulunamadı.");

        if (new FileInfo(fullPath).Length > 60_000)
            throw new Exception("Dosya bu demo için çok büyük.");

        return JsonSerializer.Serialize(new
        {
            path = relativePath,
            content = File.ReadAllText(fullPath)
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