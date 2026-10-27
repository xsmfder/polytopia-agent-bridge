param([Parameter(Mandatory=$true)][string]$GameRoot,[Parameter(Mandatory=$true)][string]$OutputDirectory,[string]$SourceDirectory='', [string]$AssemblyName='PolytopiaAgentBridge.PolyMod')
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$work=Split-Path -Parent $root
if(-not $SourceDirectory){$SourceDirectory=Join-Path $work 'src'}
$gameRoot=[IO.Path]::GetFullPath($GameRoot)
$out=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $out){throw 'Build output exists; preserve evidence'}
New-Item -ItemType Directory -Path (Join-Path $out 'inputs/src') -Force | Out-Null
Add-Type -Path (Join-Path $PSHOME 'Microsoft.CodeAnalysis.dll')
Add-Type -Path (Join-Path $PSHOME 'Microsoft.CodeAnalysis.CSharp.dll')
$runtime=Join-Path $gameRoot 'dotnet'
$referencePaths=@(Get-ChildItem -LiteralPath $runtime -Filter '*.dll' -File | Where-Object Name -notin @('coreclr.dll','clrjit.dll','clretwrc.dll','dbgshim.dll','mscordaccore.dll','mscordbi.dll','msquic.dll','hostpolicy.dll') | Select-Object -ExpandProperty FullName)
$referencePaths+=@(Get-ChildItem -LiteralPath (Join-Path $gameRoot 'BepInEx/core') -Filter '*.dll' -File | Where-Object Name -notin @('dobby.dll') | Select-Object -ExpandProperty FullName)
$referencePaths+=@(Get-ChildItem -LiteralPath (Join-Path $gameRoot 'BepInEx/interop') -Filter '*.dll' -File | Select-Object -ExpandProperty FullName)
$referencePaths+=Join-Path $gameRoot 'BepInEx/plugins/PolyMod.dll'
$references=[Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
$refRows=@()
foreach($reference in $referencePaths){
    try {[Reflection.AssemblyName]::GetAssemblyName($reference) | Out-Null}
    catch [System.BadImageFormatException] {continue}
    try {$references.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($reference))}
    catch {throw ('Invalid compile reference '+$reference+': '+$_)}
    $refRows+=[pscustomobject]@{path=$reference;sha256=(Get-FileHash -LiteralPath $reference -Algorithm SHA256).Hash.ToLowerInvariant()}
}
$parseOptions=[Microsoft.CodeAnalysis.CSharp.CSharpParseOptions]::Default.WithPreprocessorSymbols([string[]]@('BRIDGE_POLYMOD'))
$trees=[Collections.Generic.List[Microsoft.CodeAnalysis.SyntaxTree]]::new()
$sourceRows=@()
foreach($source in Get-ChildItem -LiteralPath $SourceDirectory -Filter '*.cs' -File | Sort-Object Name){
    $copy=Join-Path $out ('inputs/src/'+$source.Name)
    Copy-Item -LiteralPath $source.FullName -Destination $copy
    $trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($copy),$parseOptions,$copy))
    $sourceRows+=[pscustomobject]@{path=('src/'+$source.Name);sha256=(Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash.ToLowerInvariant()}
}
$options=[Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary)
$options=$options.WithOptimizationLevel([Microsoft.CodeAnalysis.OptimizationLevel]::Release).WithPlatform([Microsoft.CodeAnalysis.Platform]::X64)
$compilation=[Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create($AssemblyName,$trees,$references,$options)
$dll=Join-Path $out ($AssemblyName+'.dll')
$stream=[IO.File]::Create($dll)
try {$result=$compilation.Emit($stream)} finally {$stream.Dispose()}
$diagnostics=@($result.Diagnostics | ForEach-Object ToString)
$diagnostics | Set-Content -LiteralPath (Join-Path $out 'compile.log') -Encoding UTF8
$record=[ordered]@{ok=$result.Success;targetFramework='net6.0';symbols=@('BRIDGE_POLYMOD');compiler=(Join-Path $PSHOME 'Microsoft.CodeAnalysis.CSharp.dll');sources=$sourceRows;references=$refRows;candidate=$dll;dllSha256=$null}
if($result.Success){$record.dllSha256=(Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLowerInvariant()}
$record | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $out 'build.json') -Encoding UTF8
if(-not $result.Success){$diagnostics | Select-Object -Last 18;throw 'PolyMod compilation failed; inspect preserved build log'}
Write-Output ('PolyMod compile PASS; '+$sourceRows.Count+' sources, '+$references.Count+' references; '+$record.dllSha256)
