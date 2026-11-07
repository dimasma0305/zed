using System.Text.Json;

namespace Zyber.Languages;

public sealed record LanguageDefinition(string Id, string Name, string Server, string Command, string[] Arguments, string? NodeScript = null);

public static class LanguageServerRegistry
{
    public static LanguageDefinition? ForFile(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".py" or ".pyi" => new("python", "Python", "pyright", "pyright-langserver", ["--stdio"], "pyright/langserver.index.js"),
        ".ts" => new("typescript", "TypeScript", "typescript-language-server", "typescript-language-server", ["--stdio"], "typescript-language-server/lib/cli.mjs"),
        ".tsx" => new("typescriptreact", "TSX", "typescript-language-server", "typescript-language-server", ["--stdio"], "typescript-language-server/lib/cli.mjs"),
        ".js" or ".mjs" or ".cjs" => new("javascript", "JavaScript", "typescript-language-server", "typescript-language-server", ["--stdio"], "typescript-language-server/lib/cli.mjs"),
        ".jsx" => new("javascriptreact", "JSX", "typescript-language-server", "typescript-language-server", ["--stdio"], "typescript-language-server/lib/cli.mjs"),
        ".rs" => new("rust", "Rust", "rust-analyzer", "rust-analyzer", []),
        ".cs" => new("csharp", "C#", "csharp-ls", "csharp-ls", []),
        ".go" => new("go", "Go", "gopls", "gopls", []),
        ".c" or ".h" => new("c", "C", "clangd", "clangd", []),
        ".cc" or ".cpp" or ".cxx" or ".hpp" or ".hh" => new("cpp", "C++", "clangd", "clangd", []),
        _ => null
    };

    public static LanguageServerConfiguration? Resolve(LanguageDefinition language, string? settingsPath = null)
    {
        var command = language.Command;
        IReadOnlyList<string> arguments = language.Arguments;
        JsonElement settings = default;
        settingsPath ??= DefaultSettingsPath;
        if (File.Exists(settingsPath))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(settingsPath), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                if (document.RootElement.TryGetProperty("lsp", out var servers) && servers.TryGetProperty(language.Server, out var server))
                {
                    if (server.TryGetProperty("settings", out var configuredSettings)) settings = configuredSettings.Clone();
                    if (server.TryGetProperty("binary", out var binary))
                    {
                        if (binary.TryGetProperty("path", out var path)) command = path.GetString() ?? throw new IOException("Language server binary.path must be a string.");
                        if (binary.TryGetProperty("arguments", out var configuredArguments))
                            arguments = configuredArguments.EnumerateArray().Select(argument => argument.GetString() ?? throw new IOException("Language server arguments must be strings.")).ToArray();
                    }
                }
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException)
            {
                throw new IOException("Invalid language-server settings: " + exception.Message, exception);
            }
        }
        var executable = FindExecutable(command);
        if (executable is null) return null;
        if (OperatingSystem.IsWindows() && Path.GetExtension(executable).Equals(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            // npm's Windows shims are shell scripts. Launch their known JavaScript entry point directly.
            var directory = Path.GetDirectoryName(executable)!;
            var candidates = language.NodeScript is null ? [] : new[]
            {
                Path.Combine(directory, "node_modules", language.NodeScript),
                Path.Combine(directory, "..", language.NodeScript)
            };
            var script = candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
            var node = FindExecutable("node");
            if (script is null || node is null) throw new IOException("Configure lsp." + language.Server + ".binary.path as an executable, with script arguments if needed.");
            return new(language.Server, node, new[] { script }.Concat(arguments).ToArray(), settings);
        }
        return new(language.Server, executable, arguments, settings);
    }
    public static string DefaultSettingsPath => Path.Combine(
        OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        OperatingSystem.IsWindows() ? "Zyber" : "zyber", "settings.json");

    public static string? FindExecutable(string command)
    {
        if (Path.IsPathRooted(command)) return File.Exists(command) ? command : null;
        if (command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar))
            throw new IOException("Language server binary paths must be absolute or commands on PATH.");
        var suffixes = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", "" } : new[] { "" };
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var suffix in suffixes)
            {
                var candidate = Path.Combine(directory, command + suffix);
                if (File.Exists(candidate)) return candidate;
            }
        return null;
    }
}
