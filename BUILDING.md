# Building

This repository contains the client-side source of **Combat Augmentation System** (SPT 4.1.6).

## Prerequisites

- .NET SDK (the project targets `netstandard2.1`)
- An SPT 4.1.6 client install (to obtain reference assemblies)

## Reference assemblies

Copy the following DLLs from your SPT install into a local `libs/` folder
(this folder is git-ignored and must never be committed — the assemblies are
not redistributable):

| File | Source |
| --- | --- |
| `BepInEx.dll` | `BepInEx/core/` |
| `0Harmony.dll` | `BepInEx/core/` |
| `spt-reflection.dll` | `BepInEx/core/` |
| `hollowed.dll` | SPT client reference assembly (Assembly-CSharp, deobfuscated) |
| `Comfort.dll` | `EscapeFromTarkov_Data/Managed/` |
| `Sirenix.Serialization.dll` | `EscapeFromTarkov_Data/Managed/` |
| `Sirenix.Serialization.Config.dll` | `EscapeFromTarkov_Data/Managed/` |
| `Sirenix.Utilities.dll` | `EscapeFromTarkov_Data/Managed/` |
| `UnityEngine.dll` | `EscapeFromTarkov_Data/Managed/` |
| `UnityEngine.CoreModule.dll` | `EscapeFromTarkov_Data/Managed/` |
| `UnityEngine.IMGUIModule.dll` | `EscapeFromTarkov_Data/Managed/` |
| `UnityEngine.InputLegacyModule.dll` | `EscapeFromTarkov_Data/Managed/` |
| `UnityEngine.PhysicsModule.dll` | `EscapeFromTarkov_Data/Managed/` |
| `UnityEngine.TextRenderingModule.dll` | `EscapeFromTarkov_Data/Managed/` |
| `UnityEngine.UI.dll` | `EscapeFromTarkov_Data/Managed/` |
| `UnityEngine.UIModule.dll` | `EscapeFromTarkov_Data/Managed/` |

## Build

```
dotnet build -c Release
```

Output: `bin/Release/netstandard2.1/CombatAugmentationSystem.dll`

## Install

Copy `CombatAugmentationSystem.dll` to `BepInEx/plugins/CombatAugmentationSystem/` in your SPT install.
