$ErrorActionPreference = 'Stop'
Add-Type -Path (Join-Path $PSScriptRoot 'Despertador.cs') -ReferencedAssemblies 'System.Windows.Forms.dll','System.Drawing.dll','System.Xml.Linq.dll','System.Xml.dll','System.Core.dll' -OutputAssembly (Join-Path $PSScriptRoot 'Despertador.exe') -OutputType WindowsApplication
