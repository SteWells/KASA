# KASA Plugin — Compilation Guide

The body discovery system requires compiling `KASADiscovery.cs` into a DLL.
The resulting `KASA.dll` goes into `GameData/KASA/Plugins/`.

---

## Critical: Target Framework

**Use .NET Framework 4.7.2** — not 3.5.

KSP (modern versions) and ContractConfigurator are both built against .NET 4.x.
Targeting 3.5 causes the cascade of MSB3258/MSB3274 warnings and prevents the
types from Assembly-CSharp and ContractConfigurator from being resolved.

In Visual Studio: right-click project → Properties → Application →
Target Framework → **.NET Framework 4.7.2** → Save, then rebuild.

---

## Referenced DLLs

From your KSP install:

| DLL | Location |
|-----|----------|
| `Assembly-CSharp.dll` | `KSP_Root\KSP_Data\Managed\` |
| `UnityEngine.dll` | `KSP_Root\KSP_Data\Managed\` |
| `UnityEngine.CoreModule.dll` | `KSP_Root\KSP_Data\Managed\` |

From Contract Configurator (must be installed in your KSP):

| DLL | Location |
|-----|----------|
| `ContractConfigurator.dll` | `GameData\ContractConfigurator\Plugins\` |

For all four references, set **Copy Local = False** so the KSP DLLs are not
bundled into your output folder.

---

## Ready-made project file

Create `KASA.csproj` in the same folder as `KASADiscovery.cs`.
Replace the two PATH_TO_KSP placeholders with your actual KSP install path,
e.g. `C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program`

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net472</TargetFramework>
    <AssemblyName>KASA</AssemblyName>
    <Nullable>disable</Nullable>
    <LangVersion>7.3</LangVersion>
    <Optimize>true</Optimize>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="Assembly-CSharp">
      <HintPath>PATH_TO_KSP\KSP_Data\Managed\Assembly-CSharp.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="UnityEngine">
      <HintPath>PATH_TO_KSP\KSP_Data\Managed\UnityEngine.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="UnityEngine.CoreModule">
      <HintPath>PATH_TO_KSP\KSP_Data\Managed\UnityEngine.CoreModule.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="ContractConfigurator">
      <HintPath>PATH_TO_KSP\GameData\ContractConfigurator\Plugins\ContractConfigurator.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>
  <ItemGroup>
    <Compile Include="KASADiscovery.cs" />
  </ItemGroup>
</Project>
```

---

## Step-by-step: Visual Studio

1. Create a new **Class Library (.NET Framework)** project
2. **Immediately** change Target Framework to **.NET Framework 4.7.2**
   (Project → Properties → Application → Target Framework → Save)
3. **Set platform to x64** to match KSP and avoid MSB3270 warning:
   - Open Configuration Manager (Build menu → Configuration Manager)
   - Under "Active solution platform", click `<New...>`
   - Choose **x64**, copy settings from Any CPU, click OK
   - Make sure your project row also shows x64
4. Delete the auto-generated `Class1.cs`
5. Add → Existing Item → `KASADiscovery.cs`
6. Right-click References → Add Reference → Browse → add all four DLLs above
7. For each reference, open Properties and set **Copy Local = False**
8. Build → Release
9. Copy `bin\Release\KASA.dll` → `GameData\KASA\Plugins\KASA.dll`

---

## Step-by-step: .NET SDK command line

With `KASA.csproj` and `KASADiscovery.cs` in the same folder:

```
dotnet build KASA.csproj -c Release
```

Then copy `bin\Release\net472\KASA.dll` to `GameData\KASA\Plugins\`.

---

## Expected build output

A clean build at net472 should produce **0 errors, 0 warnings**.

---

## File layout after compilation

```
GameData/
  KASA/
    KASA.cfg
    KASA_Discovery.cfg
    Agencies/
      Agents.cfg
    Contracts/
      01_TheFirstFlights.cfg
      02_TheDiscoveryProgram.cfg
      03_TheKerbinSystemProgram.cfg
    Plugins/
      KASA.dll              <- compiled output goes here (KSP only loads from Plugins/)
    Plugin/
      KASADiscovery.cs      <- source only, not needed at runtime
      KASA.csproj
      COMPILING.md
```

---

## Attribution

Body masking system adapted from **ResearchBodies** by Jamie Leighton (MIT Licence).
https://github.com/JPLRepo/ResearchBodies
