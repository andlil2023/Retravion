using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Retravion.Web.Features.Mcp;

internal static class TerminalPackages
{
    internal const string Version = "1.0.0";
    // Fixed embedded resources, never a directory walk or any user/credential/record content.
    private static readonly string[] Files = ["SKILL.md", "README.md", "mcp.example.json"];

    internal static TerminalBundle? Get(string client)
    {
        if (client is not ("pi" or "claude")) return null;
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var file in Files)
            {
                using var input = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Retravion.TerminalSkills.{file}")
                    ?? throw new IOException("Shipped terminal skill files are unavailable.");
                using var reader = new StreamReader(input, Encoding.UTF8);
                var text = reader.ReadToEnd();
                if (file == "README.md") text = text.Replace("%CLIENT%", client == "pi" ? "Pi" : "Claude Code")
                    .Replace("%SKILL_PATH%", client == "pi" ? ".pi/skills/retravion" : ".claude/skills/retravion")
                    .Replace("%CONFIG_PATH%", client == "pi" ? ".pi/mcp.json" : ".mcp.json")
                    .Replace("%EXECUTABLE%", client == "pi" ? "pi" : "claude")
                    .Replace("%SKILL_COMMAND%", client == "pi" ? "/skill:retravion" : "/retravion");
                var entry = zip.CreateEntry(file == "SKILL.md" ? "skills/retravion/SKILL.md" : file, CompressionLevel.Fastest);
                entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var output = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                output.Write(text);
            }
        return new(client, stream.ToArray());
    }
}

internal sealed record TerminalBundle(string Client, byte[] Bytes)
{
    internal string FileName => $"retravion-{Client}-{TerminalPackages.Version}.zip";
    internal string Sha256 => Convert.ToHexString(SHA256.HashData(Bytes));
    internal string DownloadPath => $"/settings/ai-workflow/packages/{TerminalPackages.Version}/{Client}";
}
