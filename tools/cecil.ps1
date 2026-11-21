# 只读分析工具：用 BepInEx 自带的 Mono.Cecil 读取游戏程序集结构
param(
    [string]$Asm = 'D:\Application\游戏\Casualties Unknown V1.0.2\CasualtiesUnknown_Data\Managed\Assembly-CSharp.dll',
    [string]$Type = '',
    [string]$Method = '',
    [switch]$ListTypes
)

[Console]::OutputEncoding = [Text.Encoding]::UTF8
$cecil = 'D:\Application\游戏\Casualties Unknown V1.0.2\BepInEx\core\Mono.Cecil.dll'
Add-Type -Path $cecil

$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($Asm)

if ($ListTypes) {
    $asm.MainModule.Types | Where-Object { $_.Name -match $Type } | ForEach-Object {
        Write-Host ("TYPE " + $_.FullName)
    }
    return
}

$types = @($asm.MainModule.Types | Where-Object { $_.FullName -eq $Type })
if ($types.Count -eq 0) { $types = @($asm.MainModule.Types | Where-Object { $_.Name -eq $Type }) }
if ($types.Count -eq 0) { Write-Host "TYPE NOT FOUND: $Type"; return }

foreach ($t in $types) {
    Write-Host ("==== TYPE " + $t.FullName + " : " + $t.BaseType)
    foreach ($f in $t.Fields) { Write-Host ("  FIELD " + $f.FieldType.Name + " " + $f.Name) }
    foreach ($p in $t.Properties) { Write-Host ("  PROP " + $p.PropertyType.Name + " " + $p.Name) }
    foreach ($m in $t.Methods) {
        $ps = ($m.Parameters | ForEach-Object { $_.ParameterType.Name + ' ' + $_.Name }) -join ', '
        Write-Host ("  METHOD " + $m.ReturnType.Name + " " + $m.Name + "(" + $ps + ")")
    }
}

if ($Method) {
    foreach ($t in $types) {
        foreach ($m in $t.Methods) {
            if ($m.Name -ne $Method) { continue }
            Write-Host ("==== IL " + $t.FullName + "::" + $m.Name)
            if (-not $m.HasBody) { Write-Host '  <no body>'; continue }
            foreach ($i in $m.Body.Instructions) {
                Write-Host ("  " + $i.Offset.ToString("X4") + " " + $i.OpCode + " " + $i.Operand)
            }
        }
    }
}