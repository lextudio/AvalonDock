---
title: Installation
layout: default
parent: Getting Started
nav_order: 1
description: "Install AvalonDock via NuGet and add it to your WPF project."
---

# Installation

AvalonDock is distributed as a set of NuGet packages. The core package contains the docking framework, and theme packages are installed separately.

For the LibreWPF fork, authenticate the NuGet client to GitHub Packages. Map the
five fork package IDs to lextudio GitHub Packages and the LibreWPF runtime closure
to the OpenAvalon-published package closure before restoring. GitHub Packages
uses the lextudio owner endpoint for these packages. CI can use its built-in
`GITHUB_TOKEN`; local OpenDevelop builds use the logged-in `gh` CLI to synchronize
a local feed without writing a token into the repository:

```bash
gh auth login
```

```xml
<packageSources>
  <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  <add key="openavalon-github" value="https://nuget.pkg.github.com/lextudio/index.json" />
</packageSources>
<packageSourceMapping>
  <packageSource key="nuget.org">
    <package pattern="ProGPU.Scene.Native" />
    <package pattern="*" />
  </packageSource>
  <packageSource key="openavalon-github">
    <package pattern="Dirkster.AvalonDock.Core" />
    <package pattern="Dirkster.AvalonDock.LibreWPF" />
    <package pattern="Dirkster.AvalonDock.Serializer.Xml" />
    <package pattern="Dirkster.AvalonDock.Themes.VS.LibreWPF" />
    <package pattern="Dirkster.AvalonDock.Themes.VS2013.LibreWPF" />
    <package pattern="LibreWPF.*" />
    <package pattern="LibreWinForms.*" />
    <package pattern="ProGPU.*" />
  </packageSource>
</packageSourceMapping>
```

---

## Install via NuGet

Also add `<package pattern="ProGPU.Scene.Native" />` to your
`nuget.org` source mapping. This exact rule overrides `ProGPU.*` above:
the package is published on NuGet.org outside OpenAvalon's GitHub closure.

### .NET CLI

```bash
dotnet add package Dirkster.AvalonDock.LibreWPF
```

### Package Manager Console

```powershell
Install-Package Dirkster.AvalonDock.LibreWPF
```

### PackageReference (csproj)

```xml
<PackageReference Include="Dirkster.AvalonDock.LibreWPF" Version="5.0.0" />
```

---

## Install a Theme

AvalonDock ships with a default generic theme, but you'll likely want one of the polished built-in themes. Install the theme package for your preferred look:

```bash
# Visual Studio 2013 theme
dotnet add package Dirkster.AvalonDock.Themes.VS2013.LibreWPF

# Visual Studio theme
dotnet add package Dirkster.AvalonDock.Themes.VS.LibreWPF
```

---

## Optional Packages

For advanced scenarios, AvalonDock provides additional packages:

| Package | Install Command | Purpose |
|:--------|:----------------|:--------|
| Serializer (XML) | `dotnet add package Dirkster.AvalonDock.Serializer.Xml` | Save/restore layouts in XML format |
| Serializer (JSON) | `dotnet add package Dirkster.AvalonDock.Serializer.Json` | Save/restore layouts in JSON format |
| MVVM | `dotnet add package Dirkster.AvalonDock.Mvvm` | MVVM base classes and services (no external dependencies) |
| MVVM + CommunityToolkit | `dotnet add package Dirkster.AvalonDock.Mvvm.CommunityToolkit` | `ObservableObject`-based classes with `[ObservableProperty]` support |
| DI | `dotnet add package Dirkster.AvalonDock.DependencyInjection` | Dependency injection integration |

---

## Verify Installation

After installing, add the AvalonDock namespace to your XAML:

```xml
<Window xmlns:avalonDock="https://github.com/Dirkster99/AvalonDock">
    <avalonDock:DockingManager>
        <avalonDock:LayoutRoot>
            <avalonDock:LayoutPanel>
                <avalonDock:LayoutDocumentPane />
            </avalonDock:LayoutPanel>
        </avalonDock:LayoutRoot>
    </avalonDock:DockingManager>
</Window>
```

If the project builds without errors, you're ready to go. Head to the [Quick Start]({% link getting-started/quick-start.md %}) guide to build your first layout.

---

## Target Framework Compatibility

| Framework | Supported |
|:----------|:----------|
| .NET 10 (`net10.0-windows`) | ✅ |
| .NET 9 (`net9.0-windows`) | ✅ |
| .NET Framework 4.8 (`net48`) | ✅ |

{: .note }
Make sure your project targets one of the supported frameworks. AvalonDock requires the `-windows` TFM suffix for .NET 9+ projects.
