using System;
using System.Collections.Generic;

namespace VNotch.Models;

public sealed class AppItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string TargetPath { get; set; } = "";
    public string? Arguments { get; set; }
    public string? CustomIconPath { get; set; }
    public string? ColorHex { get; set; }
}

public sealed class AppFolder
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string? ColorHex { get; set; }
    public List<AppItem> Apps { get; set; } = new();
}

public sealed class AppFoldersConfig
{
    public List<AppFolder> Folders { get; set; } = new();
}
