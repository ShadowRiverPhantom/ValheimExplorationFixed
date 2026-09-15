<#
.SYNOPSIS
    Repairs the Exploration 1.0.4 plugin for current Valheim builds.

.DESCRIPTION
    Three separate defects are fixed at IL level, so no source or recompile is required:

      1. The Harmony target string "RPC_OpenRespons" no longer exists - Valheim renamed the method
         to RPC_OpenResponse. HarmonyX's PatchAll does not wrapped each patch in try/catch, so the
         exception it throws aborts every patch declared after it (wishbone radius, and
         SkillManager's IsSkillValid). Without IsSkillValid, Skills.Load() drops custom skills and
         the Exploration level resets to zero on every load.

      2. MultiplyTreasure compares the treasure level threshold the wrong way round: the shipped
         IL branches when the skill is *below* the configured level, so chest multiplication fires
         exactly when it should not.

      3. Several patch methods dereference Player.m_localPlayer without a null check. On a
         dedicated server there is no local player, so those paths throw NullReferenceException.

    Requires Mono.Cecil (ships with BepInEx).

.PARAMETER SrcDll
    The released Exploration.dll to repair.

.PARAMETER OutDll
    Where the repaired plugin is written. Must not be the same file as SrcDll.

.PARAMETER BepInExCoreDir
    BepInEx core directory that contains Mono.Cecil.dll. Defaults to $env:BEPINEX_CORE_DIR.

.EXAMPLE
    .\fix-exploration.ps1 -SrcDll .\upstream\Exploration.dll -OutDll .\dist\Exploration.dll
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$SrcDll,
    [Parameter(Mandatory = $true)][string]$OutDll,
    [string]$BepInExCoreDir
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $SrcDll)) { throw "Source plugin not found: $SrcDll" }
if ([System.IO.Path]::GetFullPath($SrcDll) -eq [System.IO.Path]::GetFullPath($OutDll)) {
    throw 'OutDll must not be the same file as SrcDll.'
}

function Find-Core {
    param([string]$Explicit)

    $candidates = New-Object System.Collections.Generic.List[string]
    if ($Explicit) { $candidates.Add($Explicit) }
    if ($env:BEPINEX_CORE_DIR) { $candidates.Add($env:BEPINEX_CORE_DIR) }
    if ($env:VALHEIM_DIR) { $candidates.Add((Join-Path $env:VALHEIM_DIR 'BepInEx\core')) }
    $candidates.Add((Join-Path $env:APPDATA 'r2modmanPlus-local\Valheim\profiles\Default\BepInEx\core'))
    $candidates.Add('C:\Program Files (x86)\Steam\steamapps\common\Valheim\BepInEx\core')

    $cacheRoot = Join-Path $env:APPDATA 'r2modmanPlus-local\Valheim\cache'
    if (Test-Path $cacheRoot) {
        $pack = Get-ChildItem -Path $cacheRoot -Recurse -Filter 'Mono.Cecil.dll' -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($pack) { $candidates.Add($pack.DirectoryName) }
    }

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path (Join-Path $candidate 'Mono.Cecil.dll'))) {
            return (Resolve-Path $candidate).Path
        }
    }
    throw 'BepInEx core not found. Install BepInExPack_Valheim or pass -BepInExCoreDir.'
}

$core = Find-Core -Explicit $BepInExCoreDir
Add-Type -Path (Join-Path $core 'Mono.Cecil.dll') | Out-Null

$code = [Mono.Cecil.Cil.Code]
$opcodes = [Mono.Cecil.Cil.OpCodes]
$instruction = [Mono.Cecil.Cil.Instruction]

function Get-TypeRecursive($types, [string]$full) {
    foreach ($type in $types) {
        if ($type.FullName -eq $full) { return $type }
        $found = Get-TypeRecursive $type.NestedTypes $full
        if ($found) { return $found }
    }
    return $null
}

function Get-MethodNamed($type, [string]$name) {
    foreach ($method in $type.Methods) { if ($method.Name -eq $name) { return $method } }
    return $null
}

function Get-LocalPlayerFieldRef($body) {
    foreach ($i in $body.Instructions) {
        if ($i.OpCode.Code -eq $code::Ldsfld -and $i.Operand -is [Mono.Cecil.FieldReference] -and $i.Operand.Name -eq 'm_localPlayer') {
            return $i.Operand
        }
    }
    return $null
}

# Prefixes the method with:  if (Player.m_localPlayer == null) { <nullPath> }
function Add-NullGuard($method, $nullPath) {
    $body = $method.Body
    $field = Get-LocalPlayerFieldRef $body
    if ($null -eq $field) { throw "m_localPlayer field reference not found in $($method.FullName)" }

    $first = $body.Instructions[0]
    $load = $instruction::Create($opcodes::Ldsfld, $field)
    $branch = $instruction::Create($opcodes::Brtrue_S, $first)
    $body.Instructions.Insert(0, $branch)
    $body.Instructions.Insert(0, $load)
    $index = 2
    foreach ($x in $nullPath) { $body.Instructions.Insert($index, $x); $index++ }
    Write-Host ("  [null guard] {0}" -f $method.FullName)
}

$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($SrcDll)
$exploration = Get-TypeRecursive $assembly.MainModule.Types 'Exploration.Exploration'
if ($null -eq $exploration) { throw 'Exploration.Exploration type not found.' }

# ---------------------------------------------------------------- FIX 1
# [HarmonyPatch(typeof(Container), "RPC_OpenRespons")] -> "RPC_OpenResponse"
Write-Host 'FIX 1 - restore the Harmony patch target that Valheim renamed'
$multiply = Get-TypeRecursive $exploration.NestedTypes 'Exploration.Exploration/MultiplyTreasure'
$renamed = $false
foreach ($attribute in $multiply.CustomAttributes) {
    if ($attribute.AttributeType.Name -ne 'HarmonyPatch') { continue }
    $arguments = $attribute.ConstructorArguments
    for ($i = 0; $i -lt $arguments.Count; $i++) {
        if ($arguments[$i].Value -is [string] -and $arguments[$i].Value -eq 'RPC_OpenRespons') {
            $arguments[$i] = New-Object Mono.Cecil.CustomAttributeArgument($arguments[$i].Type, 'RPC_OpenResponse')
            $renamed = $true
            Write-Host '  RPC_OpenRespons -> RPC_OpenResponse'
        }
    }
}
if (-not $renamed) { throw 'HarmonyPatch target string not found - the plugin is not the expected build.' }

# ---------------------------------------------------------------- FIX 2
# The treasure chest level test is inverted: blt.un.s (skip when A < B) -> bgt.un.s (skip when A > B)
Write-Host 'FIX 2 - invert the treasure multiplication level test'
$prefix = Get-MethodNamed $multiply 'Prefix'
$branches = @($prefix.Body.Instructions | Where-Object { $_.OpCode.Code -eq $code::Blt_Un_S })
if ($branches.Count -ne 1) { throw "Expected exactly 1 blt.un.s, found $($branches.Count)." }
$branches[0].OpCode = $opcodes::Bgt_Un_S
Write-Host ("  IL_{0:X4}: blt.un.s -> bgt.un.s (target IL_{1:X4})" -f $branches[0].Offset, $branches[0].Operand.Offset)

# ---------------------------------------------------------------- FIX 3
# A dedicated server has no local player; guard every patch method that dereferences it.
Write-Host 'FIX 3 - guard the Player.m_localPlayer dereferences'
$returnVoid = @($instruction::Create($opcodes::Ret))
$returnTrue = @($instruction::Create($opcodes::Ldc_I4_1), $instruction::Create($opcodes::Ret))
$returnFirstArgument = @($instruction::Create($opcodes::Ldarg_0), $instruction::Create($opcodes::Ret))

Add-NullGuard (Get-MethodNamed (Get-TypeRecursive $exploration.NestedTypes 'Exploration.Exploration/IncreaseExplorationRadius') 'Prefix') $returnVoid
Add-NullGuard (Get-MethodNamed (Get-TypeRecursive $exploration.NestedTypes 'Exploration.Exploration/PreventMapTableUsageWrite') 'Prefix') $returnTrue
Add-NullGuard (Get-MethodNamed (Get-TypeRecursive $exploration.NestedTypes 'Exploration.Exploration/PreventMapTableUsageRead') 'Prefix') $returnTrue
Add-NullGuard (Get-MethodNamed (Get-TypeRecursive $exploration.NestedTypes 'Exploration.Exploration/UpdateHoverTextWrite') 'Postfix') $returnVoid
Add-NullGuard (Get-MethodNamed (Get-TypeRecursive $exploration.NestedTypes 'Exploration.Exploration/UpdateHoverTextRead') 'Postfix') $returnVoid
Add-NullGuard (Get-MethodNamed (Get-TypeRecursive $exploration.NestedTypes 'Exploration.Exploration/AlterBeaconRange') 'ModifyBeaconRange') $returnFirstArgument

# ---------------------------------------------------------------- write
$directory = Split-Path $OutDll -Parent
if ($directory -and -not (Test-Path $directory)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }

$assembly.Write($OutDll)
$assembly.Dispose()

Write-Host ''
Write-Host ("-> {0} ({1} bytes)" -f $OutDll, (Get-Item $OutDll).Length) -ForegroundColor Green
