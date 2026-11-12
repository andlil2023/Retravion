using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Retravion.Web.Features.Mcp;

internal static class TerminalPackages
{
    internal const string Version = "2.9.0";
    // Fixed embedded resources, never a directory walk or any user/credential/record content.
    private static readonly (string Resource, string Entry)[] Files =
    [
        ("SKILL.md", "skills/retravion/SKILL.md"), ("README.md", "README.md"), ("mcp_setup.ps1", "mcp_setup.ps1"),
        ("bridge.server.mjs", "bridge/server.mjs"), ("bridge.tools.json", "bridge/tools.json")
    ];

    internal static TerminalBundle? Get(string client)
    {
        if (client is not ("pi" or "claude")) return null;
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (file, name) in Files)
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
                // Each download's setup script defaults to its own client.
                if (file == "mcp_setup.ps1") text = text.Replace("%CLIENT_ID%", client);
                var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
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
