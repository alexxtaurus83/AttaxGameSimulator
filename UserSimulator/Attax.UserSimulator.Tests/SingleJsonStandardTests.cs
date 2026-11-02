using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Attax.UserSimulator.Tests;

public class SingleJsonStandardTests
{
    private static readonly string[] ForbiddenTokens =
    {
        "System.Text.Json",
        "System.Net.Http.Json",
        "JsonSerializerOptions",
        "ReadFromJsonAsync",
        "PostAsJsonAsync",
        "GetFromJsonAsync",
        "JsonContent.Create",
        "AddJsonProtocol"
    };

    [Fact]
    public void NoForbiddenJsonTokens_ExistInUserSimulator()
    {
        // Root of UserSimulator folder
        var simDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        Assert.True(Directory.Exists(simDir), $"Simulator directory '{simDir}' not found.");

        var csFiles = Directory.GetFiles(simDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) &&
                        !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) &&
                        !Path.GetFileName(f).Equals("SingleJsonStandardTests.cs", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var violations = new List<string>();

        foreach (var file in csFiles)
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                foreach (var token in ForbiddenTokens)
                {
                    if (line.Contains(token, StringComparison.Ordinal))
                    {
                        violations.Add($"{Path.GetRelativePath(simDir, file)}:{i + 1} contains forbidden token '{token}'");
                    }
                }
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void NoLocalJsonSerializerSettings_DefinedInUserSimulator()
    {
        var simDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var csFiles = Directory.GetFiles(simDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) &&
                        !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) &&
                        !Path.GetFileName(f).Equals("SingleJsonStandardTests.cs", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var violations = new List<string>();

        foreach (var file in csFiles)
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.Contains("new JsonSerializerSettings", StringComparison.Ordinal))
                {
                    violations.Add($"{Path.GetRelativePath(simDir, file)}:{i + 1} defines local JsonSerializerSettings. Use AttaxJsonSettings or AttaxRulesJsonSettings from Contracts.");
                }
            }
        }

        Assert.Empty(violations);
    }
}
